using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
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
    public const string WaitForGeneration = "wait-for-generation";
}

internal static class ReconcilePlanner
{
    internal sealed record Outcome(
        string Action,
        string? FailureReason = null,
        string? DiscardReason = null,
        bool RequiresGenerationRetry = false,
        bool IncrementProposeRetry = false,
        AiGenerationAttemptSnapshot? HydrateFromAttempt = null);

    internal static Outcome Decide(
        TicketSuggestionSaga saga,
        AutoSuggestionOptions options,
        AutoSuggestionReconcileResult reconcile,
        AiGenerationAttemptSnapshot? attempt = null,
        DateTimeOffset? now = null) =>
        reconcile.Decision switch
        {
            AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob =>
                new Outcome(ReconcileActions.Complete),
            AutoSuggestionReconcileDecision.StillSuggestible =>
                DecideStillSuggestible(saga, options, attempt, now ?? DateTimeOffset.UtcNow),
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

    private static Outcome DecideStillSuggestible(
        TicketSuggestionSaga saga,
        AutoSuggestionOptions options,
        AiGenerationAttemptSnapshot? attempt,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(saga.GeneratedSuggestion))
        {
            if (saga.ProposeRetryCount >= options.MaxProposeRetries)
            {
                return new Outcome(ReconcileActions.Fail, "Applying suggestion timed out after retries.");
            }

            return new Outcome(ReconcileActions.Propose, IncrementProposeRetry: true);
        }

        var attemptOutcome = GenerationAttemptReconcileEvaluator.Evaluate(saga, options, attempt, now);
        return attemptOutcome ?? new Outcome(ReconcileActions.Fail, "Generation timed out after retries.");
    }
}

internal static class GenerationAttemptReconcileEvaluator
{
    internal static ReconcilePlanner.Outcome? Evaluate(
        TicketSuggestionSaga saga,
        AutoSuggestionOptions options,
        AiGenerationAttemptSnapshot? attempt,
        DateTimeOffset now)
    {
        if (attempt is null)
        {
            var grace = TimeSpan.FromSeconds(Math.Max(5, options.MissingAttemptGraceSeconds));
            if (now - saga.CurrentAttemptIssuedAt <= grace)
                return new ReconcilePlanner.Outcome(ReconcileActions.WaitForGeneration);

            return DecideRetryOrFail(saga, options, "Generation request never enqueued.");
        }

        return attempt.Status switch
        {
            AiGenerationAttemptStatus.Pending =>
                new ReconcilePlanner.Outcome(ReconcileActions.WaitForGeneration),
            AiGenerationAttemptStatus.Running when IsHardTimeoutExceeded(attempt, options, now) =>
                DecideRetryOrFail(saga, options, "AI generation exceeded hard timeout."),
            AiGenerationAttemptStatus.Running when attempt.LeaseUntil is not null && attempt.LeaseUntil > now =>
                new ReconcilePlanner.Outcome(ReconcileActions.WaitForGeneration),
            AiGenerationAttemptStatus.Running =>
                DecideRetryOrFail(saga, options, "AI generation lease expired."),
            AiGenerationAttemptStatus.Completed =>
                new ReconcilePlanner.Outcome(ReconcileActions.Propose, HydrateFromAttempt: attempt),
            AiGenerationAttemptStatus.Failed =>
                DecideRetryOrFail(saga, options, attempt.Error ?? "AI generation failed."),
            AiGenerationAttemptStatus.Superseded =>
                DecideRetryOrFail(saga, options, attempt.Error ?? "AI generation superseded."),
            _ => DecideRetryOrFail(saga, options, $"Unexpected attempt status: {attempt.Status}")
        };
    }

    private static bool IsHardTimeoutExceeded(
        AiGenerationAttemptSnapshot attempt,
        AutoSuggestionOptions options,
        DateTimeOffset now)
    {
        var hardTimeoutSeconds = Math.Max(options.AiGenerationLeaseSeconds, options.AiGenerationHardTimeoutSeconds);
        return now - attempt.StartedAt > TimeSpan.FromSeconds(hardTimeoutSeconds);
    }

    private static ReconcilePlanner.Outcome DecideRetryOrFail(
        TicketSuggestionSaga saga,
        AutoSuggestionOptions options,
        string failureReason)
    {
        if (saga.RetryCount < options.MaxGenerationRetries)
            return new ReconcilePlanner.Outcome(ReconcileActions.Retry, RequiresGenerationRetry: true);

        return new ReconcilePlanner.Outcome(ReconcileActions.Fail, failureReason);
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
        context.Saga.CurrentAttemptIssuedAt = now;
        context.Saga.RowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
    }

    internal static void StartNewAttempt(TicketSuggestionSaga saga)
    {
        saga.CurrentAttemptId = Guid.NewGuid();
        saga.CurrentAttemptIssuedAt = DateTimeOffset.UtcNow;
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

    internal static GenerationCheck CreateGenerationCheck(BehaviorContext<TicketSuggestionSaga> context) =>
        new(context.Saga.CorrelationId, context.Saga.CurrentAttemptId);

    internal static void HydrateFromAttempt(TicketSuggestionSaga saga, AiGenerationAttemptSnapshot attempt)
    {
        saga.GeneratedCategory = attempt.Category;
        saga.GeneratedSuggestion = attempt.Suggestion;
        saga.GeneratedRelatedDocumentsJson = attempt.RelatedDocumentsJson;
        saga.UpdatedAt = DateTimeOffset.UtcNow;
    }

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

    internal static void BeginReconciling(TicketSuggestionSaga saga) =>
        ReconcileTransientTracker.BeginReconciling(saga, DateTimeOffset.UtcNow);

    internal static void BeginReconciling(BehaviorContext<TicketSuggestionSaga> context) =>
        BeginReconciling(context.Saga);

}
