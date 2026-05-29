namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

public enum SagaTimeoutOutcome
{
    Complete,
    Proceed,
    RetryVerify,
    ResendMark,
    ResendRun,
    ResendSave,
    ResendCompensate,
    Compensate,
    Fail
}
