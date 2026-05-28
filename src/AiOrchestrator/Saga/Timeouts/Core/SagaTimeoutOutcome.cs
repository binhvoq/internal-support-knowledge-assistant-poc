namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

public enum SagaTimeoutOutcome
{
    Complete,
    RetryVerify,
    ResendSave,
    Compensate,
    Fail
}
