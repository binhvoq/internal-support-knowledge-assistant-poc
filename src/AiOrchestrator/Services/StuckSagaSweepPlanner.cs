using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

internal static class StuckSagaSweepPlanner
{
    internal enum SweepActionType
    {
        ReconcileRetry,
        FinalReconcileCandidate,
        StuckStepSweep,
        ReconcileUnknownRedrive
    }

    internal sealed record SweepAction(SweepActionType Type, TicketSuggestionSaga Saga, string? Reason = null);

    internal static IReadOnlyList<SweepAction> Plan(
        IEnumerable<TicketSuggestionSaga> sagas,
        IReadOnlyDictionary<Guid, SagaReconciliationItem> reconciliationItems,
        AutoSuggestionOptions options,
        DateTimeOffset now)
    {
        var retryAfter = TimeSpan.FromMinutes(Math.Max(1, options.StuckReconcilingRetryAfterMinutes));
        var failAfter = TimeSpan.FromMinutes(Math.Max(options.StuckReconcilingRetryAfterMinutes + 1, options.StuckReconcilingFailAfterMinutes));
        var stuckStepAfter = TimeSpan.FromMinutes(Math.Max(1, options.StuckStepSweepAfterMinutes));
        var unknownRedriveAfter = TimeSpan.FromMinutes(Math.Max(1, options.ReconcileUnknownRedriveAfterMinutes));
        var maxUnknownRedrives = Math.Max(1, options.MaxReconcileUnknownRedriveAttempts);
        var actions = new List<SweepAction>();

        foreach (var saga in sagas)
        {
            var age = now - saga.UpdatedAt; // keep for non-reconciling states and general telemetry
            switch (saga.CurrentState)
            {
                case SagaProcessState.Reconciling:
                    if (!ReconcileTransientTracker.IsBackoffElapsed(saga, options, now))
                        break;

                    var reconcilingAge = ReconcileTransientTracker.GetReconcilingAge(saga, now);
                    if (reconcilingAge >= failAfter)
                    {
                        actions.Add(new SweepAction(
                            SweepActionType.FinalReconcileCandidate,
                            saga,
                            $"Reconciling abandoned after {failAfter.TotalMinutes:0} minutes without recovery. Check DLQ and TicketService availability."));
                    }
                    else if (reconcilingAge >= retryAfter)
                    {
                        actions.Add(new SweepAction(SweepActionType.ReconcileRetry, saga));
                    }

                    break;
                case SagaProcessState.GeneratingSuggestion or SagaProcessState.ApplyingSuggestion:
                    if (age >= stuckStepAfter)
                        actions.Add(new SweepAction(SweepActionType.StuckStepSweep, saga));
                    break;
                case SagaProcessState.ReconcileUnknown:
                    if (!reconciliationItems.TryGetValue(saga.CorrelationId, out var item))
                        break;
                    if (item.ResolvedAt is not null)
                        break;
                    if (item.AttemptCount >= maxUnknownRedrives)
                        break;
                    if (now - item.CreatedAt < unknownRedriveAfter)
                        break;
                    if (!ReconcileTransientTracker.IsUnknownBackoffElapsed(
                            item.LastAttemptAt,
                            item.AttemptCount,
                            options,
                            now))
                        break;

                    actions.Add(new SweepAction(SweepActionType.ReconcileUnknownRedrive, saga));
                    break;
            }
        }

        return actions;
    }
}
