namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

public sealed record SagaTimeoutDecision(SagaTimeoutOutcome Outcome, string Reason)
{
    public static SagaTimeoutDecision Complete(string reason) => new(SagaTimeoutOutcome.Complete, reason);
    public static SagaTimeoutDecision RetryVerify(string reason) => new(SagaTimeoutOutcome.RetryVerify, reason);
    public static SagaTimeoutDecision ResendSave(string reason) => new(SagaTimeoutOutcome.ResendSave, reason);
    public static SagaTimeoutDecision Compensate(string reason) => new(SagaTimeoutOutcome.Compensate, reason);
    public static SagaTimeoutDecision Fail(string reason) => new(SagaTimeoutOutcome.Fail, reason);
}
