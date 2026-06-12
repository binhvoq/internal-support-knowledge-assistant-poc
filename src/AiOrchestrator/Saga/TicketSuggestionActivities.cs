using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga;

internal static class ReconcileActions
{
    public const string Propose = "propose";
    public const string Retry = "retry";
    public const string Fail = "fail";
}

internal static class ReconcilePlanner
{
    internal sealed record Outcome(
        string Action,
        string? FailureReason = null,
        bool StartNewGenerationAttempt = false,
        bool IncrementProposeRetry = false);

    internal static Outcome Decide(TicketSuggestionSaga saga, AutoSuggestionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(saga.GeneratedSuggestion))
        {
            if (saga.ProposeRetryCount >= options.MaxProposeRetries)
            {
                return new Outcome(ReconcileActions.Fail, "Applying suggestion timed out after retries.");
            }

            return new Outcome(ReconcileActions.Propose, IncrementProposeRetry: true);
        }

        if (saga.RetryCount < options.MaxGenerationRetries)
        {
            return new Outcome(ReconcileActions.Retry, StartNewGenerationAttempt: true);
        }

        return new Outcome(ReconcileActions.Fail, "Generation timed out after retries.");
    }
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
        context.Saga.RowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
    }

    internal static void StartNewAttempt(BehaviorContext<TicketSuggestionSaga> context)
    {
        context.Saga.CurrentAttemptId = Guid.NewGuid();
        context.Saga.GeneratedCategory = null;
        context.Saga.GeneratedSuggestion = null;
        context.Saga.GeneratedRelatedDocumentsJson = "[]";
        context.Saga.PendingReconcileAction = null;
        context.Saga.ProposeRetryCount = 0;
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

    internal static void Reconcile(BehaviorContext<TicketSuggestionSaga> context)
    {
        var logger = context.GetServiceOrCreateInstance<ILogger<TicketSuggestionStateMachine>>();
        var options = context.GetServiceOrCreateInstance<IOptions<AutoSuggestionOptions>>().Value;

        context.Saga.PendingReconcileAction = null;
        var outcome = ReconcilePlanner.Decide(context.Saga, options);

        if (outcome.IncrementProposeRetry)
        {
            context.Saga.ProposeRetryCount++;
            logger.LogInformation(
                "Reconcile: retry propose via state machine SagaId={SagaId} ProposeRetryCount={ProposeRetryCount}",
                context.Saga.CorrelationId,
                context.Saga.ProposeRetryCount);
        }

        if (outcome.StartNewGenerationAttempt)
        {
            context.Saga.RetryCount++;
            StartNewAttempt(context);
        }

        if (outcome.FailureReason is not null)
        {
            context.Saga.FailureReason = outcome.FailureReason;
        }

        context.Saga.PendingReconcileAction = outcome.Action;
        context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
