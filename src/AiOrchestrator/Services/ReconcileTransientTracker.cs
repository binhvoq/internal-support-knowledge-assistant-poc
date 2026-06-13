using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Services;

internal static class ReconcileTransientTracker
{
    internal static TimeSpan ComputeBackoffDelay(int failureCount, AutoSuggestionOptions options)
    {
        var baseSeconds = Math.Max(1, options.ReconcileTransientBackoffBaseSeconds);
        var maxSeconds = Math.Max(baseSeconds, options.ReconcileTransientBackoffMaxSeconds);
        var exponent = Math.Min(Math.Max(0, failureCount), 20);
        var delaySeconds = Math.Min(baseSeconds * Math.Pow(2, exponent), maxSeconds);
        return TimeSpan.FromSeconds(delaySeconds);
    }

    internal static bool IsBackoffElapsed(TicketSuggestionSaga saga, AutoSuggestionOptions options, DateTimeOffset now)
    {
        if (saga.LastReconcileAttemptAt is null)
            return true;

        var delay = ComputeBackoffDelay(saga.ReconcileTransientFailureCount, options);
        return now - saga.LastReconcileAttemptAt.Value >= delay;
    }

    internal static DateTimeOffset GetReconcilingAgeAnchor(TicketSuggestionSaga saga) =>
        saga.ReconcilingSinceAt ?? saga.UpdatedAt;

    internal static TimeSpan GetReconcilingAge(TicketSuggestionSaga saga, DateTimeOffset now) =>
        now - GetReconcilingAgeAnchor(saga);

    internal static void BeginReconciling(TicketSuggestionSaga saga, DateTimeOffset now)
    {
        saga.ReconcilingSinceAt ??= now;
        saga.UpdatedAt = now;
    }

    internal static void EnsureReconcilingSince(TicketSuggestionSaga saga, DateTimeOffset now)
    {
        saga.ReconcilingSinceAt ??= now;
    }

    internal static void RecordScheduledAttempt(TicketSuggestionSaga saga, DateTimeOffset now)
    {
        EnsureReconcilingSince(saga, now);
        saga.LastReconcileAttemptAt = now;
        // Intentionally do NOT overwrite UpdatedAt here: UpdatedAt may be refreshed by retries,
        // but the reconciling age clock (for failAfter/escalate) is anchored at ReconcilingSinceAt.
    }

    internal static void RecordTransientFailure(TicketSuggestionSaga saga, DateTimeOffset now)
    {
        EnsureReconcilingSince(saga, now);
        saga.ReconcileTransientFailureCount++;
        saga.LastReconcileAttemptAt = now;
        // Intentionally do NOT overwrite UpdatedAt: preserve entry anchor for age-based decisions.
    }

    internal static void RecordSuccess(TicketSuggestionSaga saga, DateTimeOffset now)
    {
        saga.ReconcileTransientFailureCount = 0;
        saga.ReconcilingSinceAt = null;
        saga.UpdatedAt = now;
    }

    internal static bool ShouldEscalate(TicketSuggestionSaga saga, AutoSuggestionOptions options, DateTimeOffset now)
    {
        if (saga.ReconcileTransientFailureCount <= 0)
            return false;

        if (saga.ReconcileTransientFailureCount >= Math.Max(1, options.MaxReconcileTransientFailuresBeforeEscalate))
            return true;

        var escalateAfter = TimeSpan.FromMinutes(Math.Max(1, options.StuckReconcilingEscalateAfterMinutes));
        var since = saga.ReconcilingSinceAt ?? saga.UpdatedAt;
        return now - since >= escalateAfter;
    }

    internal static TimeSpan ComputeUnknownBackoffDelay(int attemptCount, AutoSuggestionOptions options)
    {
        var baseSeconds = Math.Max(1, options.ReconcileUnknownBackoffBaseSeconds);
        var maxSeconds = Math.Max(baseSeconds, options.ReconcileUnknownBackoffMaxSeconds);
        var exponent = Math.Min(Math.Max(0, attemptCount), 20);
        var delaySeconds = Math.Min(baseSeconds * Math.Pow(2, exponent), maxSeconds);
        return TimeSpan.FromSeconds(delaySeconds);
    }

    internal static bool IsUnknownBackoffElapsed(
        DateTimeOffset? lastAttemptAt,
        int attemptCount,
        AutoSuggestionOptions options,
        DateTimeOffset now)
    {
        if (lastAttemptAt is null)
            return true;

        var delay = ComputeUnknownBackoffDelay(attemptCount, options);
        return now - lastAttemptAt.Value >= delay;
    }
}
