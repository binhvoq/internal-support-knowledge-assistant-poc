namespace SupportPoc.AiOrchestrator.Saga;

// Structured log fields for Azure Monitor / App Insights queries later (e.g. customDimensions.SagaId).
internal static class SagaCompensationDiagnostics
{
    public const string EventCompensationFailed = "SagaCompensationFailed";
    public const string EventProbeUnavailable = "SagaCompensationProbeUnavailable";

    public static void LogCompensationFailed(
        ILogger logger,
        TicketSuggestionState saga,
        string reason,
        string? probeError = null)
    {
        logger.LogError(
            "{EventName} SagaId={SagaId} TicketId={TicketId} Reason={Reason} ProbeError={ProbeError} " +
            "CompensationReason={CompensationReason} VerifyAttempts={VerifyAttempts} " +
            "PostResendVerifyAttempts={PostResendVerifyAttempts} CompensateResendCount={CompensateResendCount}",
            EventCompensationFailed,
            saga.CorrelationId,
            saga.TicketId,
            reason,
            probeError ?? string.Empty,
            saga.CompensationReason ?? string.Empty,
            saga.TimeoutVerifyAttempts,
            saga.PostResendVerifyAttempts,
            saga.CompensateResendCount);
    }

    public static void LogProbeUnavailable(
        ILogger logger,
        TicketSuggestionState saga,
        string reason,
        int verifyAttempt,
        int maxAttempts)
    {
        logger.LogWarning(
            "{EventName} SagaId={SagaId} TicketId={TicketId} Reason={Reason} VerifyAttempt={VerifyAttempt} MaxAttempts={MaxAttempts}",
            EventProbeUnavailable,
            saga.CorrelationId,
            saga.TicketId,
            reason,
            verifyAttempt,
            maxAttempts);
    }
}
