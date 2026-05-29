namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

public sealed record SagaTimeoutDecision(
    SagaTimeoutOutcome Outcome,
    string Reason,
    int? ResolvedTicketSagaEpoch = null,
    string? RecoveredCategory = null,
    string? RecoveredSuggestion = null,
    string? RecoveredRelatedDocumentsJson = null)
{
    public static SagaTimeoutDecision Complete(string reason) => new(SagaTimeoutOutcome.Complete, reason);

    public static SagaTimeoutDecision Proceed(string reason, int ticketSagaEpoch) =>
        new(SagaTimeoutOutcome.Proceed, reason, ticketSagaEpoch);

    public static SagaTimeoutDecision ProceedFromTicketDraft(
        string reason,
        int ticketSagaEpoch,
        string category,
        string suggestion,
        string relatedDocumentsJson) =>
        new(
            SagaTimeoutOutcome.Proceed,
            reason,
            ticketSagaEpoch,
            category,
            suggestion,
            relatedDocumentsJson);

    public static SagaTimeoutDecision RetryVerify(string reason) => new(SagaTimeoutOutcome.RetryVerify, reason);

    public static SagaTimeoutDecision ResendMark(string reason) => new(SagaTimeoutOutcome.ResendMark, reason);

    public static SagaTimeoutDecision ResendRun(string reason) => new(SagaTimeoutOutcome.ResendRun, reason);

    public static SagaTimeoutDecision ResendSave(string reason) => new(SagaTimeoutOutcome.ResendSave, reason);

    public static SagaTimeoutDecision ResendCompensate(string reason) => new(SagaTimeoutOutcome.ResendCompensate, reason);

    public static SagaTimeoutDecision Compensate(string reason) => new(SagaTimeoutOutcome.Compensate, reason);

    public static SagaTimeoutDecision Fail(string reason) => new(SagaTimeoutOutcome.Fail, reason);
}
