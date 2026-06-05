using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Tests;

public sealed class IndexerTriggerResultTests
{
    [Fact]
    public void AlreadyRunning_outcome_is_distinct_from_failure()
    {
        var result = new IndexerTriggerResult(IndexerTriggerOutcome.AlreadyRunning, "Indexer dang chay tu truoc");

        Assert.Equal(IndexerTriggerOutcome.AlreadyRunning, result.Outcome);
        Assert.NotEqual(IndexerTriggerOutcome.Started, result.Outcome);
        Assert.Contains("dang chay", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
