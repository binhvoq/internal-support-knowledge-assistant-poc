using Microsoft.ApplicationInsights;
using SupportPoc.Shared.Telemetry;

namespace SupportPoc.AiOrchestrator.Services;

internal static class SagaReconcileTelemetry
{
    public const string DecisionEvent = "SagaReconcileDecision";
    public const string HttpFailureEvent = "SagaReconcileHttpFailure";
    public const string SweepEvent = "SagaReconcileSweep";
    public const string StuckGaugeEvent = "SagaReconcilingStuck";

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

    public static void TrackStuckCount(TelemetryClient? telemetry, int stuckCount, int retried, int abandoned) =>
        telemetry?.TrackEvent(StuckGaugeEvent, new Dictionary<string, string>
        {
            ["stuckCount"] = stuckCount.ToString(),
            ["retried"] = retried.ToString(),
            ["abandoned"] = abandoned.ToString(),
        });
}
