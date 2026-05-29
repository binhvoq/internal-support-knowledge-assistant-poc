using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

public sealed record StepTimeoutContext(
    TicketSuggestionState Saga,
    SagaStepTimeoutOptions Step,
    int VerifyAttempt,
    int PostResendVerifyAttempt,
    int ResendCount);
