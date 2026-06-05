using SupportPoc.KnowledgeService.Data;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Services;

public sealed class DocumentIngestionStatusRefresher
{
    private readonly AzureSearchIngestionService _ingestion;
    private readonly KnowledgeSearchService _search;
    private readonly AzureSearchOptions _searchOptions;

    public DocumentIngestionStatusRefresher(
        AzureSearchIngestionService ingestion,
        KnowledgeSearchService search,
        Microsoft.Extensions.Options.IOptions<AzureSearchOptions> searchOptions)
    {
        _ingestion = ingestion;
        _search = search;
        _searchOptions = searchOptions.Value;
    }

    public async Task<IngestionPollDecision> EvaluateDocumentAsync(
        KnowledgeDocumentEntity entity,
        bool pollTimedOut,
        CancellationToken cancellationToken = default)
    {
        if (!_ingestion.IsPipelineConfigured)
        {
            return new IngestionPollDecision(
                IngestionPollAction.Processing,
                "Azure pipeline chua cau hinh.");
        }

        var snapshot = await _ingestion.GetExecutionSnapshotAsync(cancellationToken);
        var chunkCount = await _search.CountChunksByDocumentIdAsync(entity.Id, cancellationToken);
        return DocumentIngestionStatusEvaluator.Evaluate(
            snapshot.LastRunStatus,
            snapshot.IndexerRunning,
            chunkCount,
            pollTimedOut,
            entity.Id,
            snapshot.Issues);
    }

    public static void ApplyDecision(
        KnowledgeDocumentEntity entity,
        IngestionPollDecision decision,
        string indexName)
    {
        switch (decision.Action)
        {
            case IngestionPollAction.Ready:
                entity.IngestionStatus = "Ready";
                entity.IngestionMessage = $"{decision.Message} Index={indexName}.";
                entity.IngestedAt = DateTimeOffset.UtcNow;
                break;
            case IngestionPollAction.Failed:
                entity.IngestionStatus = "Failed";
                entity.IngestionMessage = decision.Message;
                break;
            case IngestionPollAction.Processing:
            case IngestionPollAction.Continue:
            default:
                entity.IngestionStatus = "Processing";
                entity.IngestionMessage = decision.Message;
                break;
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
