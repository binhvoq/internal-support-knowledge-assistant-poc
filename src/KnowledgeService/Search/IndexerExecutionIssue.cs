namespace SupportPoc.KnowledgeService.Search;

public sealed record IndexerExecutionIssue(string Key, string Message, bool IsError);

public sealed record IndexerExecutionSnapshot(
    bool IndexerRunning,
    Azure.Search.Documents.Indexes.Models.IndexerExecutionStatus? LastRunStatus,
    string? LastRunErrorMessage,
    IReadOnlyList<IndexerExecutionIssue> Issues)
{
    public IReadOnlyList<IndexerExecutionIssue> IssuesForDocument(string documentId) =>
        Issues.Where(issue => IndexerIssueMatcher.MatchesDocument(documentId, issue.Key, issue.Message)).ToList();
}

public static class IndexerIssueMatcher
{
    public static bool MatchesDocument(string documentId, string? key, string? message, string? details = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            return false;

        var haystack = $"{key}\n{message}\n{details}";
        return haystack.Contains(documentId, StringComparison.OrdinalIgnoreCase)
            || haystack.Contains($"{documentId}/", StringComparison.OrdinalIgnoreCase);
    }
}
