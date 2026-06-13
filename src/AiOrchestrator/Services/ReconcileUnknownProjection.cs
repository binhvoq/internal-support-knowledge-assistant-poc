using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Services;

internal static class ReconcileUnknownProjection
{
    internal const string StatusPending = "pending";
    internal const string StatusExhausted = "exhausted";
    internal const string StatusMissingItem = "missing-item";

    internal sealed record UnknownSagaView(
        Guid SagaId,
        string TicketId,
        Guid JobId,
        string? FailureReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ReconciliationCreatedAt,
        DateTimeOffset? LastAttemptAt,
        int AttemptCount,
        int MaxAutoRedriveAttempts,
        string Status,
        DateTimeOffset? NextAutoRedriveEligibleAt);

    internal static string ResolveStatus(SagaReconciliationItem? item, int maxAttempts)
    {
        if (item is null)
            return StatusMissingItem;
        if (item.ResolvedAt is not null)
            return "resolved";
        if (item.AttemptCount >= maxAttempts)
            return StatusExhausted;
        return StatusPending;
    }

    internal static DateTimeOffset? ComputeNextAutoRedriveEligibleAt(
        SagaReconciliationItem? item,
        AutoSuggestionOptions options,
        DateTimeOffset now)
    {
        if (item is null || item.ResolvedAt is not null)
            return null;

        var maxAttempts = Math.Max(1, options.MaxReconcileUnknownRedriveAttempts);
        if (item.AttemptCount >= maxAttempts)
            return null;

        var redriveAfter = TimeSpan.FromMinutes(Math.Max(1, options.ReconcileUnknownRedriveAfterMinutes));
        var parkedEligibleAt = item.CreatedAt + redriveAfter;

        if (item.LastAttemptAt is null)
            return parkedEligibleAt > now ? parkedEligibleAt : now;

        var backoffEligibleAt = item.LastAttemptAt.Value
            + ReconcileTransientTracker.ComputeUnknownBackoffDelay(item.AttemptCount, options);

        return parkedEligibleAt > backoffEligibleAt ? parkedEligibleAt : backoffEligibleAt;
    }

    internal static UnknownSagaView Project(
        TicketSuggestionSaga saga,
        SagaReconciliationItem? item,
        AutoSuggestionOptions options,
        DateTimeOffset now)
    {
        var maxAttempts = Math.Max(1, options.MaxReconcileUnknownRedriveAttempts);
        return new UnknownSagaView(
            saga.CorrelationId,
            saga.TicketId,
            saga.JobId,
            saga.FailureReason,
            saga.CreatedAt,
            saga.UpdatedAt,
            item?.CreatedAt,
            item?.LastAttemptAt,
            item?.AttemptCount ?? 0,
            maxAttempts,
            ResolveStatus(item, maxAttempts),
            ComputeNextAutoRedriveEligibleAt(item, options, now));
    }

    internal static IEnumerable<UnknownSagaView> OrderForOps(IEnumerable<UnknownSagaView> views) =>
        views
            .OrderByDescending(v => v.Status == StatusExhausted)
            .ThenBy(v => v.UpdatedAt);
}
