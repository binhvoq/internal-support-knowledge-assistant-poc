using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

internal static class StuckSagaSweepPlanner
{
    internal enum SweepActionType
    {
        ReconcileRetry,
        AbandonCandidate,
        StuckStepSweep
    }

    internal sealed record SweepAction(SweepActionType Type, TicketSuggestionSaga Saga, string? Reason = null);

    internal static IReadOnlyList<SweepAction> Plan(
        IEnumerable<TicketSuggestionSaga> sagas,
        AutoSuggestionOptions options,
        DateTimeOffset now)
    {
        var retryAfter = TimeSpan.FromMinutes(Math.Max(1, options.StuckReconcilingRetryAfterMinutes));
        var failAfter = TimeSpan.FromMinutes(Math.Max(options.StuckReconcilingRetryAfterMinutes + 1, options.StuckReconcilingFailAfterMinutes));
        var stuckStepAfter = TimeSpan.FromMinutes(Math.Max(1, options.StuckStepSweepAfterMinutes));
        var actions = new List<SweepAction>();

        foreach (var saga in sagas)
        {
            var age = now - saga.UpdatedAt;
            switch (saga.CurrentState)
            {
                case SagaProcessState.Reconciling:
                    if (age >= failAfter)
                    {
                        actions.Add(new SweepAction(
                            SweepActionType.AbandonCandidate,
                            saga,
                            $"Reconciling abandoned after {failAfter.TotalMinutes:0} minutes without recovery. Check DLQ and TicketService availability."));
                    }
                    else if (age >= retryAfter)
                    {
                        actions.Add(new SweepAction(SweepActionType.ReconcileRetry, saga));
                    }
                    break;
                case SagaProcessState.GeneratingSuggestion or SagaProcessState.ApplyingSuggestion:
                    if (age >= stuckStepAfter)
                        actions.Add(new SweepAction(SweepActionType.StuckStepSweep, saga));
                    break;
            }
        }

        return actions;
    }
}
