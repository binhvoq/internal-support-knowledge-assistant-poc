using MassTransit;
using Moq;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class GenerationAttemptReconcileTests
{
    private static readonly AutoSuggestionOptions DefaultOptions = new()
    {
        MaxGenerationRetries = 2,
        MaxProposeRetries = 2,
        AiGenerationLeaseSeconds = 300,
        AiGenerationHardTimeoutSeconds = 1800,
        MissingAttemptGraceSeconds = 60
    };

    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decide_waits_when_attempt_running_with_active_lease()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var attempt = RunningAttempt(attemptId, leaseUntil: Now.AddMinutes(5));

        var outcome = DecideStillSuggestible(saga, attempt);

        Assert.Equal(ReconcileActions.WaitForGeneration, outcome.Action);
        Assert.False(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void Decide_waits_when_attempt_pending()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var attempt = Attempt(attemptId, AiGenerationAttemptStatus.Pending);

        var outcome = DecideStillSuggestible(saga, attempt);

        Assert.Equal(ReconcileActions.WaitForGeneration, outcome.Action);
        Assert.False(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void Decide_waits_when_attempt_row_missing_within_grace_period()
    {
        var saga = NewSaga(Guid.NewGuid(), retryCount: 0);
        saga.CurrentAttemptIssuedAt = Now;

        var outcome = ReconcilePlanner.Decide(
            saga,
            DefaultOptions,
            Reconcile(AutoSuggestionReconcileDecision.StillSuggestible),
            attempt: null,
            Now);

        Assert.Equal(ReconcileActions.WaitForGeneration, outcome.Action);
        Assert.False(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void Decide_retries_when_attempt_row_missing_past_grace_period()
    {
        var saga = NewSaga(Guid.NewGuid(), retryCount: 0);
        saga.CurrentAttemptIssuedAt = Now.AddSeconds(-DefaultOptions.MissingAttemptGraceSeconds - 1);

        var outcome = ReconcilePlanner.Decide(
            saga,
            DefaultOptions,
            Reconcile(AutoSuggestionReconcileDecision.StillSuggestible),
            attempt: null,
            Now);

        Assert.Equal(ReconcileActions.Retry, outcome.Action);
        Assert.True(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void Decide_proposes_and_hydrates_when_attempt_completed()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var attempt = Attempt(
            attemptId,
            AiGenerationAttemptStatus.Completed,
            category: "Billing",
            suggestion: "Try resetting password.",
            relatedJson: """[{"documentId":"d1","title":"FAQ"}]""");

        var outcome = DecideStillSuggestible(saga, attempt);

        Assert.Equal(ReconcileActions.Propose, outcome.Action);
        Assert.False(outcome.RequiresGenerationRetry);
        Assert.False(outcome.IncrementProposeRetry);
        Assert.NotNull(outcome.HydrateFromAttempt);
        Assert.Equal("Billing", outcome.HydrateFromAttempt!.Category);
        Assert.Equal("Try resetting password.", outcome.HydrateFromAttempt.Suggestion);
    }

    [Fact]
    public void Decide_retries_when_attempt_failed_and_retries_remain()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var attempt = Attempt(attemptId, AiGenerationAttemptStatus.Failed, error: "LLM timeout");

        var outcome = DecideStillSuggestible(saga, attempt);

        Assert.Equal(ReconcileActions.Retry, outcome.Action);
        Assert.True(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void Decide_retries_when_running_lease_expired()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var attempt = RunningAttempt(attemptId, leaseUntil: Now.AddMinutes(-1));

        var outcome = DecideStillSuggestible(saga, attempt);

        Assert.Equal(ReconcileActions.Retry, outcome.Action);
        Assert.True(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void Decide_retries_when_running_hard_timeout_exceeded()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var attempt = RunningAttempt(
            attemptId,
            leaseUntil: Now.AddMinutes(5),
            startedAt: Now.AddSeconds(-DefaultOptions.AiGenerationHardTimeoutSeconds - 1));

        var outcome = DecideStillSuggestible(saga, attempt);

        Assert.Equal(ReconcileActions.Retry, outcome.Action);
        Assert.True(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void Decide_fails_when_attempt_failed_and_generation_retries_exhausted()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: DefaultOptions.MaxGenerationRetries);
        var attempt = Attempt(attemptId, AiGenerationAttemptStatus.Failed, error: "LLM timeout");

        var outcome = DecideStillSuggestible(saga, attempt);

        Assert.Equal(ReconcileActions.Fail, outcome.Action);
        Assert.Equal("LLM timeout", outcome.FailureReason);
        Assert.False(outcome.RequiresGenerationRetry);
    }

    [Fact]
    public void ApplyOutcome_hydrates_saga_from_completed_attempt_without_incrementing_retry()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var attempt = Attempt(
            attemptId,
            AiGenerationAttemptStatus.Completed,
            category: "Billing",
            suggestion: "Answer",
            relatedJson: "[]");
        var outcome = new ReconcilePlanner.Outcome(
            ReconcileActions.Propose,
            HydrateFromAttempt: attempt);

        var context = new Mock<BehaviorContext<TicketSuggestionSaga>>();
        context.SetupGet(x => x.Saga).Returns(saga);
        ReconcileTicketSuggestionActivity.ApplyOutcome(context.Object, outcome);

        Assert.Equal("Billing", saga.GeneratedCategory);
        Assert.Equal("Answer", saga.GeneratedSuggestion);
        Assert.Equal(0, saga.RetryCount);
        Assert.Equal(ReconcileActions.Propose, saga.PendingReconcileAction);
    }

    [Fact]
    public async Task ResolveReconcileOutcomeAsync_uses_attempt_reader_before_deciding()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 0);
        var client = new StubReconcileClient(Reconcile(AutoSuggestionReconcileDecision.StillSuggestible));
        var reader = new StubAttemptReader(RunningAttempt(attemptId, leaseUntil: Now.AddMinutes(5)));

        var (outcome, _) = await ReconcileTicketSuggestionActivity.ResolveReconcileOutcomeAsync(
            saga,
            DefaultOptions,
            client,
            reader,
            CancellationToken.None);

        Assert.Equal(ReconcileActions.WaitForGeneration, outcome.Action);
        Assert.Equal(attemptId, reader.LastAttemptId);
    }

    [Fact]
    public void Generation_evaluator_does_not_increment_retry_count_for_wait()
    {
        var attemptId = Guid.NewGuid();
        var saga = NewSaga(attemptId, retryCount: 1);
        var attempt = RunningAttempt(attemptId, leaseUntil: Now.AddMinutes(5));

        var outcome = GenerationAttemptReconcileEvaluator.Evaluate(saga, DefaultOptions, attempt, Now);

        Assert.NotNull(outcome);
        Assert.Equal(ReconcileActions.WaitForGeneration, outcome!.Action);
        Assert.False(outcome.RequiresGenerationRetry);
    }

    private static ReconcilePlanner.Outcome DecideStillSuggestible(
        TicketSuggestionSaga saga,
        AiGenerationAttemptSnapshot? attempt) =>
        ReconcilePlanner.Decide(
            saga,
            DefaultOptions,
            Reconcile(AutoSuggestionReconcileDecision.StillSuggestible),
            attempt,
            Now);

    private static AiGenerationAttemptSnapshot RunningAttempt(
        Guid attemptId,
        DateTimeOffset leaseUntil,
        DateTimeOffset? startedAt = null) =>
        Attempt(
            attemptId,
            AiGenerationAttemptStatus.Running,
            leaseUntil: leaseUntil,
            startedAt: startedAt ?? Now);

    private static AiGenerationAttemptSnapshot Attempt(
        Guid attemptId,
        string status,
        DateTimeOffset? leaseUntil = null,
        DateTimeOffset? startedAt = null,
        string? category = null,
        string? suggestion = null,
        string relatedJson = "[]",
        string? error = null) =>
        new(
            attemptId,
            status,
            leaseUntil,
            startedAt ?? Now,
            category,
            suggestion,
            relatedJson,
            error);

    private static AutoSuggestionReconcileResult Reconcile(string decision, string? reason = null) =>
        new(TestTicketIds.Default, Guid.NewGuid(), decision, reason, TicketStatus.New, 1, false, false);

    private static TicketSuggestionSaga NewSaga(Guid attemptId, int retryCount) =>
        new()
        {
            CorrelationId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            CurrentAttemptId = attemptId,
            CurrentAttemptIssuedAt = Now,
            RetryCount = retryCount
        };

    private sealed class StubReconcileClient(AutoSuggestionReconcileResult result) : ITicketSuggestionReconcileClient
    {
        public Task<AutoSuggestionReconcileResult> ReconcileAsync(
            string ticketId,
            Guid jobId,
            long? expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class StubAttemptReader(AiGenerationAttemptSnapshot? snapshot) : IAiGenerationAttemptReader
    {
        public Guid? LastAttemptId { get; private set; }

        public Task<AiGenerationAttemptSnapshot?> GetByAttemptIdAsync(
            Guid attemptId,
            CancellationToken cancellationToken = default)
        {
            LastAttemptId = attemptId;
            return Task.FromResult(snapshot);
        }
    }
}
