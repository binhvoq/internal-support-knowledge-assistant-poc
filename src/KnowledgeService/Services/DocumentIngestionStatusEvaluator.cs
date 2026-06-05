using Azure.Search.Documents.Indexes.Models;
using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Services;

public enum IngestionPollAction
{
    Continue,
    Ready,
    Failed,
    Processing
}

public sealed record IngestionPollDecision(
    IngestionPollAction Action,
    string Message,
    int ChunkCount = 0);

/// <summary>Decides document ingestion status from indexer execution state and chunk visibility.</summary>
public static class DocumentIngestionStatusEvaluator
{
    public static IngestionPollDecision Evaluate(
        IndexerExecutionStatus? lastRunStatus,
        bool indexerRunning,
        int chunkCount,
        bool pollTimedOut,
        string? documentId = null,
        IReadOnlyList<IndexerExecutionIssue>? indexerIssues = null)
    {
        if (lastRunStatus == IndexerExecutionStatus.TransientFailure)
        {
            return new IngestionPollDecision(
                IngestionPollAction.Failed,
                "Azure indexer that bai (transientFailure).");
        }

        var documentErrors = GetDocumentErrors(documentId, indexerIssues);
        if (documentErrors.Count > 0 && chunkCount == 0 && !indexerRunning && lastRunStatus != IndexerExecutionStatus.InProgress)
        {
            return new IngestionPollDecision(
                IngestionPollAction.Failed,
                $"Azure indexer co loi item cho document: {documentErrors[0].Message}");
        }

        var runInProgress = indexerRunning || lastRunStatus == IndexerExecutionStatus.InProgress;
        if (runInProgress)
        {
            if (pollTimedOut)
            {
                return new IngestionPollDecision(
                    IngestionPollAction.Processing,
                    "Azure indexer van dang chay; background refresh se tiep tuc theo doi.");
            }

            return new IngestionPollDecision(IngestionPollAction.Continue, "Dang cho indexer hoan tat.");
        }

        if (lastRunStatus == IndexerExecutionStatus.Success)
        {
            if (chunkCount > 0)
            {
                var message = documentErrors.Count > 0
                    ? $"Azure indexer hoan tat voi {chunkCount} chunk (co {documentErrors.Count} loi item khac cho document nay)."
                    : $"Azure indexer hoan tat va da tao {chunkCount} chunk.";

                return new IngestionPollDecision(
                    IngestionPollAction.Ready,
                    message,
                    chunkCount);
            }

            if (pollTimedOut)
            {
                return new IngestionPollDecision(
                    IngestionPollAction.Processing,
                    "Indexer da success nhung chua thay chunk; background refresh se tiep tuc kiem tra.");
            }

            return new IngestionPollDecision(IngestionPollAction.Continue, "Dang cho chunk xuat hien trong index.");
        }

        if (pollTimedOut)
        {
            return new IngestionPollDecision(
                IngestionPollAction.Processing,
                "Het thoi gian poll ban dau; background refresh se tiep tuc kiem tra indexer/chunk.");
        }

        return new IngestionPollDecision(IngestionPollAction.Continue, "Dang cho indexer.");
    }

    private static IReadOnlyList<IndexerExecutionIssue> GetDocumentErrors(
        string? documentId,
        IReadOnlyList<IndexerExecutionIssue>? indexerIssues)
    {
        if (string.IsNullOrWhiteSpace(documentId) || indexerIssues is null || indexerIssues.Count == 0)
            return [];

        return indexerIssues
            .Where(issue => issue.IsError && IndexerIssueMatcher.MatchesDocument(documentId, issue.Key, issue.Message))
            .ToList();
    }
}
