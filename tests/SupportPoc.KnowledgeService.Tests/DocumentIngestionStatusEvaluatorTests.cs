using Azure.Search.Documents.Indexes.Models;
using SupportPoc.KnowledgeService.Search;
using SupportPoc.KnowledgeService.Services;

namespace SupportPoc.KnowledgeService.Tests;

public sealed class DocumentIngestionStatusEvaluatorTests
{
    [Fact]
    public void Evaluate_does_not_ready_when_chunk_exists_but_indexer_still_running()
    {
        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.InProgress,
            indexerRunning: true,
            chunkCount: 3,
            pollTimedOut: false);

        Assert.Equal(IngestionPollAction.Continue, decision.Action);
        Assert.NotEqual(IngestionPollAction.Ready, decision.Action);
    }

    [Fact]
    public void Evaluate_ready_only_when_indexer_success_and_chunks_exist()
    {
        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.Success,
            indexerRunning: false,
            chunkCount: 5,
            pollTimedOut: false);

        Assert.Equal(IngestionPollAction.Ready, decision.Action);
        Assert.Equal(5, decision.ChunkCount);
    }

    [Fact]
    public void Evaluate_does_not_ready_on_chunk_count_alone_when_run_not_success()
    {
        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.InProgress,
            indexerRunning: false,
            chunkCount: 2,
            pollTimedOut: false);

        Assert.Equal(IngestionPollAction.Continue, decision.Action);
    }

    [Fact]
    public void Evaluate_failed_on_transient_failure()
    {
        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.TransientFailure,
            indexerRunning: false,
            chunkCount: 10,
            pollTimedOut: false);

        Assert.Equal(IngestionPollAction.Failed, decision.Action);
    }

    [Fact]
    public void Evaluate_processing_on_timeout_while_indexer_running()
    {
        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.InProgress,
            indexerRunning: true,
            chunkCount: 1,
            pollTimedOut: true);

        Assert.Equal(IngestionPollAction.Processing, decision.Action);
        Assert.NotEqual(IngestionPollAction.Ready, decision.Action);
        Assert.NotEqual(IngestionPollAction.Failed, decision.Action);
    }

    [Fact]
    public void Evaluate_processing_on_timeout_after_success_without_chunks()
    {
        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.Success,
            indexerRunning: false,
            chunkCount: 0,
            pollTimedOut: true);

        Assert.Equal(IngestionPollAction.Processing, decision.Action);
        Assert.Contains("background refresh", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_failed_when_document_item_error_and_no_chunks()
    {
        var issues = new[]
        {
            new IndexerExecutionIssue("DOC-007/policy.pdf", "Embedding skill failed", IsError: true)
        };

        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.Success,
            indexerRunning: false,
            chunkCount: 0,
            pollTimedOut: true,
            documentId: "DOC-007",
            indexerIssues: issues);

        Assert.Equal(IngestionPollAction.Failed, decision.Action);
        Assert.Contains("Embedding skill failed", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ready_when_chunks_exist_despite_document_item_error()
    {
        var issues = new[]
        {
            new IndexerExecutionIssue("DOC-008/policy.pdf", "Warning on one chunk", IsError: true)
        };

        var decision = DocumentIngestionStatusEvaluator.Evaluate(
            IndexerExecutionStatus.Success,
            indexerRunning: false,
            chunkCount: 4,
            pollTimedOut: false,
            documentId: "DOC-008",
            indexerIssues: issues);

        Assert.Equal(IngestionPollAction.Ready, decision.Action);
        Assert.Equal(4, decision.ChunkCount);
    }
}
