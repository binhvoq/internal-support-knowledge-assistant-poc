using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class StuckSagaSweeperTests
{
    private static readonly AutoSuggestionOptions DefaultOptions = new()
    {
        StuckReconcilingRetryAfterMinutes = 2,
        StuckReconcilingFailAfterMinutes = 30,
        StuckStepSweepAfterMinutes = 15
    };

    [Fact]
    public void Plan_retries_stale_reconciling_saga()
    {
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.ReconcileRetry, actions[0].Type);
    }

    [Fact]
    public void Plan_marks_critically_stale_reconciling_saga_as_abandon_candidate()
    {
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromMinutes(45));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.AbandonCandidate, actions[0].Type);
        Assert.Contains("abandoned", actions[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_ignores_recent_reconciling_saga()
    {
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], DefaultOptions, now);

        Assert.Empty(actions);
    }

    [Fact]
    public void Plan_sweeps_stuck_generating_saga()
    {
        var saga = Saga(SagaProcessState.GeneratingSuggestion, TimeSpan.FromMinutes(20));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.StuckStepSweep, actions[0].Type);
    }

    [Fact]
    public void Plan_sweeps_stuck_applying_saga()
    {
        var saga = Saga(SagaProcessState.ApplyingSuggestion, TimeSpan.FromMinutes(20));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.StuckStepSweep, actions[0].Type);
    }

    [Fact]
    public void Plan_ignores_recent_generating_saga()
    {
        var saga = Saga(SagaProcessState.GeneratingSuggestion, TimeSpan.FromMinutes(2));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], DefaultOptions, now);

        Assert.Empty(actions);
    }

    private static TicketSuggestionSaga Saga(string state, TimeSpan age) =>
        new()
        {
            CorrelationId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = "TCK-1",
            CurrentState = state,
            UpdatedAt = DateTimeOffset.UtcNow - age,
            CreatedAt = DateTimeOffset.UtcNow - age
        };
}
