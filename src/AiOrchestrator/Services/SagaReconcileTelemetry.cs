using Microsoft.ApplicationInsights;
using SupportPoc.Shared.Telemetry;

namespace SupportPoc.AiOrchestrator.Services;

internal static class SagaReconcileTelemetry
{
    public const string DecisionEvent = "SagaReconcileDecision";
    public const string HttpFailureEvent = "SagaReconcileHttpFailure";
    public const string SweepEvent = "SagaReconcileSweep";
    public const string StuckGaugeEvent = "SagaReconcilingStuck";
    public const string EscalatedUnknownEvent = "SagaReconcileEscalatedUnknown";
    public const string UnknownAutoRedriveEvent = "SagaReconcileUnknownAutoRedrive";
    public const string UnknownExhaustedEvent = "SagaReconcileUnknownExhausted";
    public const string UnknownManualRedriveEvent = "SagaReconcileUnknownManualRedrive";
    public const string UnknownRecoveredEvent = "SagaReconcileUnknownRecovered";
    public const string UnknownStayedParkedEvent = "SagaReconcileUnknownStayedParked";

    public static void TrackDecision(
        TelemetryClient? telemetry,
        string decision,
        string action,
        Guid sagaId,
        string ticketId) =>
        telemetry?.TrackEvent(DecisionEvent, new Dictionary<string, string>
        {
            ["decision"] = decision,
            ["action"] = action,
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
        });

    public static void TrackHttpFailure(TelemetryClient? telemetry, Guid sagaId, string ticketId, string errorType) =>
        telemetry?.TrackEvent(HttpFailureEvent, new Dictionary<string, string>
        {
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["errorType"] = errorType,
        });

    public static void TrackSweep(
        TelemetryClient? telemetry,
        string sweepAction,
        Guid sagaId,
        string ticketId,
        TimeSpan age) =>
        telemetry?.TrackEvent(SweepEvent, new Dictionary<string, string>
        {
            ["sweepAction"] = sweepAction,
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["ageMinutes"] = age.TotalMinutes.ToString("0.##"),
        });

    public static void TrackStuckCount(
        TelemetryClient? telemetry,
        int stuckCount,
        int retried,
        int abandoned,
        int escalated = 0,
        int unknownRedriven = 0,
        int unknownExhausted = 0) =>
        telemetry?.TrackEvent(StuckGaugeEvent, new Dictionary<string, string>
        {
            ["stuckCount"] = stuckCount.ToString(),
            ["retried"] = retried.ToString(),
            ["abandoned"] = abandoned.ToString(),
            ["escalated"] = escalated.ToString(),
            ["unknownRedriven"] = unknownRedriven.ToString(),
            ["unknownExhausted"] = unknownExhausted.ToString(),
        });

    public static void TrackEscalatedToUnknown(
        TelemetryClient? telemetry,
        Guid sagaId,
        string ticketId,
        string reason) =>
        telemetry?.TrackEvent(EscalatedUnknownEvent, new Dictionary<string, string>
        {
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["reason"] = reason,
        });

    public static void TrackUnknownAutoRedrive(
        TelemetryClient? telemetry,
        Guid sagaId,
        string ticketId,
        int attemptCount,
        int maxAttempts) =>
        telemetry?.TrackEvent(UnknownAutoRedriveEvent, new Dictionary<string, string>
        {
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["attemptCount"] = attemptCount.ToString(),
            ["maxAttempts"] = maxAttempts.ToString(),
        });

    public static void TrackUnknownExhausted(
        TelemetryClient? telemetry,
        Guid sagaId,
        string ticketId,
        int attemptCount,
        int maxAttempts) =>
        telemetry?.TrackEvent(UnknownExhaustedEvent, new Dictionary<string, string>
        {
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["attemptCount"] = attemptCount.ToString(),
            ["maxAttempts"] = maxAttempts.ToString(),
        });

    public static void TrackUnknownManualRedrive(
        TelemetryClient? telemetry,
        Guid sagaId,
        string ticketId,
        string callerIdentity) =>
        telemetry?.TrackEvent(UnknownManualRedriveEvent, new Dictionary<string, string>
        {
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["callerIdentity"] = callerIdentity,
        });

    public static void TrackUnknownRecovered(
        TelemetryClient? telemetry,
        Guid sagaId,
        string ticketId,
        string resolution) =>
        telemetry?.TrackEvent(UnknownRecoveredEvent, new Dictionary<string, string>
        {
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["resolution"] = resolution,
        });

    public static void TrackUnknownStayedParked(
        TelemetryClient? telemetry,
        Guid sagaId,
        string ticketId,
        string errorType) =>
        telemetry?.TrackEvent(UnknownStayedParkedEvent, new Dictionary<string, string>
        {
            ["sagaId"] = sagaId.ToString(),
            ["ticketId"] = ticketId,
            ["errorType"] = errorType,
        });
}
