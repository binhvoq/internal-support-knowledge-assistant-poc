using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class StuckSagaAbandonPolicyTests
{
    [Theory]
    [InlineData(AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob)]
    [InlineData(AutoSuggestionReconcileDecision.StillSuggestible)]
    [InlineData(AutoSuggestionReconcileDecision.Resolved)]
    [InlineData(AutoSuggestionReconcileDecision.AlreadySuggestedByOtherJob)]
    [InlineData(AutoSuggestionReconcileDecision.VersionChanged)]
    public void Decide_returns_reconcile_sweep_for_recoverable_domain_decisions(string decision)
    {
        var reconcile = new AutoSuggestionReconcileResult(TestTicketIds.Default, Guid.NewGuid(), decision, null, TicketStatus.New, 1, false, false);

        var resolved = StuckSagaAbandonPolicy.Decide(reconcile, reconcileError: null);

        Assert.Equal(StuckSagaAbandonPolicy.ResolvedAction.ReconcileSweep, resolved);
    }

    [Fact]
    public void Decide_abandons_when_ticket_not_found()
    {
        var reconcile = new AutoSuggestionReconcileResult(
            TestTicketIds.Default,
            Guid.NewGuid(),
            AutoSuggestionReconcileDecision.NotFound,
            "Ticket not found.",
            null,
            null,
            false,
            false);

        var resolved = StuckSagaAbandonPolicy.Decide(reconcile, reconcileError: null);

        Assert.Equal(StuckSagaAbandonPolicy.ResolvedAction.Abandon, resolved);
    }

    [Fact]
    public void Decide_abandons_when_reconcile_client_throws()
    {
        var resolved = StuckSagaAbandonPolicy.Decide(
            reconcile: null,
            reconcileError: new HttpRequestException("503"));

        Assert.Equal(StuckSagaAbandonPolicy.ResolvedAction.Abandon, resolved);
    }
}
