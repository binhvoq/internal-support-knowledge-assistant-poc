using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga;

internal static class ReconcileActions
{
    public const string Complete = "complete";
    public const string Discard = "discard";
    public const string Retry = "retry";
    public const string Fail = "fail";
}

internal static class TicketSuggestionActivities
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void InitializeSaga(BehaviorContext<TicketSuggestionSaga, ITicketCreated> context)
    {
        var msg = context.Message;
        var now = DateTimeOffset.UtcNow;
        context.Saga.CorrelationId = msg.JobId;
        context.Saga.JobId = msg.JobId;
        context.Saga.TicketId = msg.TicketId;
        context.Saga.EmployeeId = msg.EmployeeId;
        context.Saga.Question = msg.Question;
        context.Saga.OriginalCategory = msg.Category;
        context.Saga.TicketVersionAtStart = msg.TicketVersion;
        context.Saga.CreatedAt = now;
        context.Saga.UpdatedAt = now;
    }

    internal static void StartNewAttempt(BehaviorContext<TicketSuggestionSaga> context)
    {
        context.Saga.CurrentAttemptId = Guid.NewGuid();
        context.Saga.GeneratedCategory = null;
        context.Saga.GeneratedSuggestion = null;
        context.Saga.GeneratedRelatedDocumentsJson = "[]";
        context.Saga.PendingReconcileAction = null;
        context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static GenerateSuggestionRequested CreateGenerateRequest(BehaviorContext<TicketSuggestionSaga> context) =>
        new(
            context.Saga.CorrelationId,
            context.Saga.CurrentAttemptId,
            context.Saga.JobId,
            context.Saga.TicketId,
            context.Saga.Question,
            context.Saga.OriginalCategory);

    internal static StepTimeout CreateStepTimeout(BehaviorContext<TicketSuggestionSaga> context) =>
        new(context.Saga.CorrelationId, context.Saga.CurrentAttemptId);

    internal static void StoreGeneratedResult(BehaviorContext<TicketSuggestionSaga, ISuggestionGenerated> context)
    {
        var msg = context.Message;
        context.Saga.GeneratedCategory = msg.Category;
        context.Saga.GeneratedSuggestion = msg.Suggestion;
        context.Saga.GeneratedRelatedDocumentsJson = JsonSerializer.Serialize(msg.RelatedDocuments, JsonOptions);
        context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static ProposeTicketSuggestion CreateProposeRequest(BehaviorContext<TicketSuggestionSaga> context)
    {
        var commandId = Guid.NewGuid();
        context.Saga.LastProposeCommandId = commandId;

        IReadOnlyList<RelatedDocument> related;
        try
        {
            related = JsonSerializer.Deserialize<IReadOnlyList<RelatedDocument>>(
                context.Saga.GeneratedRelatedDocumentsJson,
                JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            related = [];
        }

        return new ProposeTicketSuggestion(
            commandId,
            context.Saga.CorrelationId,
            context.Saga.CurrentAttemptId,
            context.Saga.JobId,
            context.Saga.TicketId,
            context.Saga.GeneratedCategory ?? context.Saga.OriginalCategory,
            context.Saga.GeneratedSuggestion ?? string.Empty,
            related,
            context.Saga.TicketVersionAtStart);
    }

    internal static void AuditLateAttempt(BehaviorContext<TicketSuggestionSaga, ISuggestionGenerated> context)
    {
        var audit = $"LateMessageIgnored: attempt {context.Message.AttemptId} != current {context.Saga.CurrentAttemptId}";
        context.Saga.LateMessageAudit = audit;
        context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
        context.GetServiceOrCreateInstance<ILogger<TicketSuggestionStateMachine>>()
            .LogInformation("{Audit} SagaId={SagaId}", audit, context.Saga.CorrelationId);
    }

    internal static void AuditLateMessageIgnored(BehaviorContext<TicketSuggestionSaga, ISuggestionGenerated> context)
    {
        var audit = $"LateMessageIgnored: saga terminal state={context.Saga.CurrentState}";
        context.Saga.LateMessageAudit = audit;
        context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
        context.GetServiceOrCreateInstance<ILogger<TicketSuggestionStateMachine>>()
            .LogInformation("{Audit} SagaId={SagaId}", audit, context.Saga.CorrelationId);
    }

    internal static void AuditMissingSagaInstance<T>(ConsumeContext<T> context, string eventName)
        where T : class
    {
        var sagaId = context.Message switch
        {
            ISuggestionGenerated g => g.SagaId,
            ISuggestionGenerationFailed f => f.SagaId,
            _ => Guid.Empty
        };
        var audit = $"LateMessageIgnored: missing saga instance for {eventName} SagaId={sagaId}";
        context.GetServiceOrCreateInstance<ILogger<TicketSuggestionStateMachine>>()
            .LogInformation("{Audit}", audit);
    }

    internal static async Task ReconcileAsync(BehaviorContext<TicketSuggestionSaga> context)
    {
        var logger = context.GetServiceOrCreateInstance<ILogger<TicketSuggestionStateMachine>>();
        var snapshotClient = context.GetServiceOrCreateInstance<ITicketSnapshotClient>();
        var options = context.GetServiceOrCreateInstance<Microsoft.Extensions.Options.IOptions<AutoSuggestionOptions>>().Value;

        context.Saga.PendingReconcileAction = null;
        var snapshot = await snapshotClient.GetTicketAsync(context.Saga.TicketId, context.CancellationToken);
        if (snapshot is not null)
        {
            if (snapshot.Status == TicketStatus.Suggested && snapshot.HasAiSuggestion)
            {
                logger.LogInformation(
                    "Reconcile: ticket already suggested SagaId={SagaId} TicketId={TicketId}",
                    context.Saga.CorrelationId,
                    context.Saga.TicketId);
                context.Saga.PendingReconcileAction = ReconcileActions.Complete;
                context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                return;
            }

            if (snapshot.Status is TicketStatus.Resolved or TicketStatus.Reopened)
            {
                context.Saga.DiscardReason = $"Ticket status is {snapshot.Status}.";
                context.Saga.PendingReconcileAction = ReconcileActions.Discard;
                context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(context.Saga.GeneratedSuggestion))
        {
            logger.LogInformation(
                "Reconcile: generated result present, retry propose SagaId={SagaId}",
                context.Saga.CorrelationId);
            var propose = CreateProposeRequest(context);
            var bus = context.GetServiceOrCreateInstance<IBus>();
            var client = bus.CreateRequestClient<IProposeTicketSuggestion>(
                new Uri("queue:propose-ticket-suggestion"),
                TimeSpan.FromSeconds(Math.Max(5, options.ProposeRequestTimeoutSeconds)));
            var response = await client.GetResponse<IProposeTicketSuggestionResult>(
                propose,
                context.CancellationToken);

            if (response.Message.Accepted)
            {
                context.Saga.PendingReconcileAction = ReconcileActions.Complete;
                context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                return;
            }

            context.Saga.DiscardReason = response.Message.Reason;
            context.Saga.PendingReconcileAction = ReconcileActions.Discard;
            context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
            return;
        }

        if (context.Saga.RetryCount < options.MaxGenerationRetries)
        {
            context.Saga.RetryCount++;
            StartNewAttempt(context);
            context.Saga.PendingReconcileAction = ReconcileActions.Retry;
            return;
        }

        context.Saga.FailureReason = "Generation timed out after retries.";
        context.Saga.PendingReconcileAction = ReconcileActions.Fail;
        context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
