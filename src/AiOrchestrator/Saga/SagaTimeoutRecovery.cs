namespace SupportPoc.AiOrchestrator.Saga;

internal static class SagaTimeoutRecovery
{
    public static void ResetForNextStep(TicketSuggestionState saga)
    {
        saga.PendingTimeoutOutcome = null;
        saga.TimeoutDecisionReason = null;
        saga.TimeoutVerifyAttempts = 0;
        saga.PostResendVerifyAttempts = 0;
        saga.MarkResendIssued = false;
        saga.MarkResendIssuedAt = null;
        saga.AiRunResendCount = 0;
        saga.AiRunResendIssuedAt = null;
        saga.SaveResendIssued = false;
        saga.SaveResendIssuedAt = null;
        saga.CompensateResendCount = 0;
        saga.CompensateResendIssuedAt = null;
    }

    public static void ResetForCompensatingStep(TicketSuggestionState saga)
    {
        saga.PendingTimeoutOutcome = null;
        saga.TimeoutDecisionReason = null;
        saga.TimeoutVerifyAttempts = 0;
        saga.PostResendVerifyAttempts = 0;
        saga.CompensateResendCount = 0;
        saga.CompensateResendIssuedAt = null;
    }
}
