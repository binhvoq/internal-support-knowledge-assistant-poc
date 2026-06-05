using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Tests;

public sealed class IndexerIssueMatcherTests
{
    [Theory]
    [InlineData("DOC-001/remote-work-policy.pdf", "DOC-001", true)]
    [InlineData("https://store.blob.core.windows.net/knowledge-docs/DOC-002/file.pdf", "DOC-002", true)]
    [InlineData("DOC-003-other.pdf", "DOC-003", true)]
    [InlineData("DOC-999/file.pdf", "DOC-001", false)]
    public void MatchesDocument_detects_blob_key_for_document(string key, string documentId, bool expected) =>
        Assert.Equal(expected, IndexerIssueMatcher.MatchesDocument(documentId, key, message: null));
}
