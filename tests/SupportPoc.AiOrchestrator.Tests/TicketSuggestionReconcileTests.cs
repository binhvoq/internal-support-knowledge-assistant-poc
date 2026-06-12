using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class TicketSuggestionReconcileTests
{
    private static readonly AutoSuggestionOptions DefaultOptions = new()
    {
        MaxGenerationRetries = 2,
        MaxProposeRetries = 2
    };

    [Fact]
    public void Decide_retries_generation_when_no_suggestion_and_retries_remain()
    {
        var saga = NewSaga(retryCount: 0);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions);

        Assert.Equal(ReconcileActions.Retry, outcome.Action);
        Assert.True(outcome.StartNewGenerationAttempt);
        Assert.False(outcome.IncrementProposeRetry);
    }

    [Fact]
    public void Decide_fails_generation_when_retries_exhausted()
    {
        var saga = NewSaga(retryCount: DefaultOptions.MaxGenerationRetries);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions);

        Assert.Equal(ReconcileActions.Fail, outcome.Action);
        Assert.Equal("Generation timed out after retries.", outcome.FailureReason);
    }

    [Fact]
    public void Decide_reproposes_when_generated_suggestion_exists()
    {
        var saga = NewSaga(generatedSuggestion: "answer", proposeRetryCount: 0);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions);

        Assert.Equal(ReconcileActions.Propose, outcome.Action);
        Assert.True(outcome.IncrementProposeRetry);
        Assert.False(outcome.StartNewGenerationAttempt);
    }

    [Fact]
    public void Decide_fails_propose_when_retries_exhausted()
    {
        var saga = NewSaga(generatedSuggestion: "answer", proposeRetryCount: DefaultOptions.MaxProposeRetries);

        var outcome = ReconcilePlanner.Decide(saga, DefaultOptions);

        Assert.Equal(ReconcileActions.Fail, outcome.Action);
        Assert.Equal("Applying suggestion timed out after retries.", outcome.FailureReason);
    }

    private static TicketSuggestionSaga NewSaga(
        int retryCount = 0,
        int proposeRetryCount = 0,
        string? generatedSuggestion = null) =>
        new()
        {
            CorrelationId = Guid.NewGuid(),
            RetryCount = retryCount,
            ProposeRetryCount = proposeRetryCount,
            GeneratedSuggestion = generatedSuggestion
        };
}
