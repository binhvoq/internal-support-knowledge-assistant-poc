using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class StuckSagaAbandonPolicyTests
{
    private static readonly AutoSuggestionOptions DefaultOptions = new()
    {
        MaxReconcileTransientFailuresBeforeEscalate = 20,
        StuckReconcilingEscalateAfterMinutes = 120
    };

    [Theory]
    [InlineData(AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob)]
    [InlineData(AutoSuggestionReconcileDecision.StillSuggestible)]
    [InlineData(AutoSuggestionReconcileDecision.Resolved)]
    [InlineData(AutoSuggestionReconcileDecision.AlreadySuggestedByOtherJob)]
    [InlineData(AutoSuggestionReconcileDecision.VersionChanged)]
    public void Decide_returns_reconcile_sweep_for_recoverable_domain_decisions(string decision)
    {
        var reconcile = new AutoSuggestionReconcileResult(TestTicketIds.Default, Guid.NewGuid(), decision, null, TicketStatus.New, 1, false, false);
        var saga = NewSaga();

        var resolved = StuckSagaAbandonPolicy.Decide(reconcile, reconcileError: null, saga, DefaultOptions, DateTimeOffset.UtcNow);

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
        var saga = NewSaga();

        var resolved = StuckSagaAbandonPolicy.Decide(reconcile, reconcileError: null, saga, DefaultOptions, DateTimeOffset.UtcNow);

        Assert.Equal(StuckSagaAbandonPolicy.ResolvedAction.Abandon, resolved);
    }

    [Fact]
    public void Decide_retries_reconcile_when_reconcile_client_throws_below_threshold()
    {
        var saga = NewSaga(failureCount: 1);

        var resolved = StuckSagaAbandonPolicy.Decide(
            reconcile: null,
            reconcileError: new HttpRequestException("503"),
            saga,
            DefaultOptions,
            DateTimeOffset.UtcNow);

        Assert.Equal(StuckSagaAbandonPolicy.ResolvedAction.ReconcileSweep, resolved);
    }

    [Fact]
    public void Decide_escalates_unknown_when_transient_failures_exceed_threshold()
    {
        var saga = NewSaga(failureCount: DefaultOptions.MaxReconcileTransientFailuresBeforeEscalate);

        var resolved = StuckSagaAbandonPolicy.Decide(
            reconcile: null,
            reconcileError: new HttpRequestException("503"),
            saga,
            DefaultOptions,
            DateTimeOffset.UtcNow);

        Assert.Equal(StuckSagaAbandonPolicy.ResolvedAction.EscalateUnknown, resolved);
    }

    [Fact]
    public void Decide_escalates_unknown_when_reconciling_too_long_with_transient_failures()
    {
        var now = DateTimeOffset.UtcNow;
        var saga = NewSaga(
            failureCount: 3,
            reconcilingSinceAt: now - TimeSpan.FromMinutes(DefaultOptions.StuckReconcilingEscalateAfterMinutes + 1));

        var resolved = StuckSagaAbandonPolicy.Decide(
            reconcile: null,
            reconcileError: new HttpRequestException("503"),
            saga,
            DefaultOptions,
            now);

        Assert.Equal(StuckSagaAbandonPolicy.ResolvedAction.EscalateUnknown, resolved);
    }

    private static TicketSuggestionSaga NewSaga(
        int failureCount = 0,
        DateTimeOffset? reconcilingSinceAt = null) =>
        new()
        {
            CorrelationId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            UpdatedAt = DateTimeOffset.UtcNow,
            ReconcileTransientFailureCount = failureCount,
            ReconcilingSinceAt = reconcilingSinceAt
        };
}
