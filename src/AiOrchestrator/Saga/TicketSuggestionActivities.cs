using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga;

internal static class ReconcileActions
{
    public const string Propose = "propose";
    public const string Retry = "retry";
    public const string Complete = "complete";
    public const string Discard = "discard";
    public const string Fail = "fail";
}

internal static class ReconcilePlanner
{
    internal sealed record Outcome(
        string Action,
        string? FailureReason = null,
        string? DiscardReason = null,
        bool StartNewGenerationAttempt = false,
        bool IncrementProposeRetry = false);

    internal static Outcome Decide(
        TicketSuggestionSaga saga,
        AutoSuggestionOptions options,
        AutoSuggestionReconcileResult reconcile) =>
        reconcile.Decision switch
        {
            AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob =>
                new Outcome(ReconcileActions.Complete),
            AutoSuggestionReconcileDecision.StillSuggestible =>
                DecideStillSuggestible(saga, options),
            AutoSuggestionReconcileDecision.Resolved
            or AutoSuggestionReconcileDecision.AlreadySuggestedByOtherJob
            or AutoSuggestionReconcileDecision.VersionChanged =>
                new Outcome(ReconcileActions.Discard, DiscardReason: reconcile.Reason ?? reconcile.Decision),
            AutoSuggestionReconcileDecision.NotFound =>
                new Outcome(ReconcileActions.Fail, reconcile.Reason ?? "Ticket not found."),
            _ => new Outcome(
                ReconcileActions.Fail,
                reconcile.Reason ?? $"Unexpected reconcile decision: {reconcile.Decision}")
        };

    private static Outcome DecideStillSuggestible(TicketSuggestionSaga saga, AutoSuggestionOptions options)
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

    internal static void StartNewAttempt(TicketSuggestionSaga saga)
    {
        saga.CurrentAttemptId = Guid.NewGuid();
        saga.GeneratedCategory = null;
        saga.GeneratedSuggestion = null;
        saga.GeneratedRelatedDocumentsJson = "[]";
        saga.PendingReconcileAction = null;
        saga.ProposeRetryCount = 0;
        saga.LastProposeCommandId = null;
        saga.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static void StartNewAttempt(BehaviorContext<TicketSuggestionSaga> context) =>
        StartNewAttempt(context.Saga);

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

    internal static Guid GetOrAssignProposeCommandId(TicketSuggestionSaga saga)
    {
        var commandId = saga.LastProposeCommandId ?? Guid.NewGuid();
        saga.LastProposeCommandId = commandId;
        return commandId;
    }

    internal static ProposeTicketSuggestion CreateProposeRequest(BehaviorContext<TicketSuggestionSaga> context)
    {
        var commandId = GetOrAssignProposeCommandId(context.Saga);

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

}
