using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

public sealed record SagaTimeoutContext(
    TicketSuggestionState Saga,
    int VerifyAttempt,
    bool SaveResendIssued,
    int PostResendVerifyAttempt);
