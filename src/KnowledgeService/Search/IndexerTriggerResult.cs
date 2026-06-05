namespace SupportPoc.KnowledgeService.Search;

public enum IndexerTriggerOutcome
{
    Started,
    AlreadyRunning
}

public sealed record IndexerTriggerResult(IndexerTriggerOutcome Outcome, string? Message = null);
