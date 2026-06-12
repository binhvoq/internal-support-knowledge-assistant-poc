using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class TicketSuggestionReconcileTests
{
    private static readonly AutoSuggestionOptions DefaultOptions = new()
    {
        MaxGenerationRetries = 2,
        MaxProposeRetries = 2
    };

    [Fact]
    public void Decide_completes_when_same_job_already_applied()
    {
        var saga = NewSaga(generatedSuggestion: "answer");
        var reconcile = Reconcile(AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Complete, outcome.Action);
    }

    [Fact]
    public void Decide_discards_when_ticket_resolved()
    {
        var saga = NewSaga();
        var reconcile = Reconcile(
            AutoSuggestionReconcileDecision.Resolved,
            reason: "Ticket already has a final answer.");

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Discard, outcome.Action);
        Assert.Equal("Ticket already has a final answer.", outcome.DiscardReason);
    }

    [Fact]
    public void Decide_discards_when_already_suggested_by_other_job()
    {
        var saga = NewSaga(generatedSuggestion: "answer");
        var reconcile = Reconcile(
            AutoSuggestionReconcileDecision.AlreadySuggestedByOtherJob,
            reason: "Ticket already has an accepted AI suggestion from another job.");

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Discard, outcome.Action);
    }

    [Fact]
    public void Decide_retries_generation_when_still_suggestible_without_suggestion()
    {
        var saga = NewSaga(retryCount: 0);
        var reconcile = Reconcile(AutoSuggestionReconcileDecision.StillSuggestible);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Retry, outcome.Action);
        Assert.True(outcome.StartNewGenerationAttempt);
    }

    [Fact]
    public void Decide_reproposes_when_still_suggestible_with_generated_suggestion()
    {
        var saga = NewSaga(generatedSuggestion: "answer", proposeRetryCount: 0);
        var reconcile = Reconcile(AutoSuggestionReconcileDecision.StillSuggestible);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Propose, outcome.Action);
        Assert.True(outcome.IncrementProposeRetry);
    }

    [Fact]
    public void Decide_fails_when_ticket_not_found()
    {
        var saga = NewSaga();
        var reconcile = Reconcile(AutoSuggestionReconcileDecision.NotFound, reason: "Ticket not found.");

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Fail, outcome.Action);
        Assert.Equal("Ticket not found.", outcome.FailureReason);
    }

    [Fact]
    public void Decide_fails_generation_when_still_suggestible_but_retries_exhausted()
    {
        var saga = NewSaga(retryCount: DefaultOptions.MaxGenerationRetries);
        var reconcile = Reconcile(AutoSuggestionReconcileDecision.StillSuggestible);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Fail, outcome.Action);
        Assert.Equal("Generation timed out after retries.", outcome.FailureReason);
    }

    [Fact]
    public void Decide_fails_propose_when_still_suggestible_but_propose_retries_exhausted()
    {
        var saga = NewSaga(generatedSuggestion: "answer", proposeRetryCount: DefaultOptions.MaxProposeRetries);
        var reconcile = Reconcile(AutoSuggestionReconcileDecision.StillSuggestible);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions, reconcile);

        Assert.Equal(ReconcileActions.Fail, outcome.Action);
        Assert.Equal("Applying suggestion timed out after retries.", outcome.FailureReason);
    }

    [Fact]
    public void GetOrAssignProposeCommandId_reuses_command_id_for_same_attempt()
    {
        var saga = NewSaga(generatedSuggestion: "answer");
        var existing = Guid.NewGuid();
        saga.LastProposeCommandId = existing;

        var first = TicketSuggestionActivities.GetOrAssignProposeCommandId(saga);
        var second = TicketSuggestionActivities.GetOrAssignProposeCommandId(saga);

        Assert.Equal(existing, first);
        Assert.Equal(existing, second);
    }

    [Fact]
    public async Task ResolveReconcileOutcomeAsync_throws_when_client_fails()
    {
        var saga = NewSaga();
        var client = new ThrowingReconcileClient(new HttpRequestException("Service unavailable"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            _ = await ReconcileTicketSuggestionActivity.ResolveReconcileOutcomeAsync(
                saga,
                DefaultOptions,
                client,
                CancellationToken.None);
        });

        Assert.Equal("Service unavailable", ex.Message);
        Assert.Null(saga.PendingReconcileAction);
    }

    [Fact]
    public async Task ResolveReconcileOutcomeAsync_returns_domain_outcome_when_client_succeeds()
    {
        var saga = NewSaga(retryCount: 0);
        var client = new StubReconcileClient(Reconcile(AutoSuggestionReconcileDecision.StillSuggestible));

        var (outcome, _) = await ReconcileTicketSuggestionActivity.ResolveReconcileOutcomeAsync(
            saga,
            DefaultOptions,
            client,
            CancellationToken.None);

        Assert.Equal(ReconcileActions.Retry, outcome.Action);
    }

    [Fact]
    public void StartNewAttempt_clears_last_propose_command_id()
    {
        var saga = NewSaga(generatedSuggestion: "answer");
        saga.LastProposeCommandId = Guid.NewGuid();

        TicketSuggestionActivities.StartNewAttempt(saga);

        Assert.Null(saga.LastProposeCommandId);
        Assert.Null(saga.GeneratedSuggestion);
    }

    private static AutoSuggestionReconcileResult Reconcile(string decision, string? reason = null) =>
        new("TCK-1", Guid.NewGuid(), decision, reason, TicketStatus.New, 1, false, false);

    private sealed class ThrowingReconcileClient(Exception exception) : ITicketSuggestionReconcileClient
    {
        public Task<AutoSuggestionReconcileResult> ReconcileAsync(
            string ticketId,
            Guid jobId,
            long? expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AutoSuggestionReconcileResult>(exception);
    }

    private sealed class StubReconcileClient(AutoSuggestionReconcileResult result) : ITicketSuggestionReconcileClient
    {
        public Task<AutoSuggestionReconcileResult> ReconcileAsync(
            string ticketId,
            Guid jobId,
            long? expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static TicketSuggestionSaga NewSaga(
        int retryCount = 0,
        int proposeRetryCount = 0,
        string? generatedSuggestion = null) =>
        new()
        {
            CorrelationId = Guid.NewGuid(),
            TicketId = "TCK-1",
            RetryCount = retryCount,
            ProposeRetryCount = proposeRetryCount,
            GeneratedSuggestion = generatedSuggestion
        };

}
