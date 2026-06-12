using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

internal static class StuckSagaAbandonPolicy
{
    internal enum ResolvedAction
    {
        ReconcileSweep,
        Abandon
    }

    internal static ResolvedAction Decide(AutoSuggestionReconcileResult? reconcile, Exception? reconcileError)
    {
        if (reconcileError is not null)
            return ResolvedAction.Abandon;

        if (reconcile is null)
            return ResolvedAction.Abandon;

        return reconcile.Decision switch
        {
            AutoSuggestionReconcileDecision.NotFound => ResolvedAction.Abandon,
            AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob => ResolvedAction.ReconcileSweep,
            AutoSuggestionReconcileDecision.StillSuggestible => ResolvedAction.ReconcileSweep,
            AutoSuggestionReconcileDecision.Resolved => ResolvedAction.ReconcileSweep,
            AutoSuggestionReconcileDecision.AlreadySuggestedByOtherJob => ResolvedAction.ReconcileSweep,
            AutoSuggestionReconcileDecision.VersionChanged => ResolvedAction.ReconcileSweep,
            _ => ResolvedAction.Abandon
        };
    }
}
