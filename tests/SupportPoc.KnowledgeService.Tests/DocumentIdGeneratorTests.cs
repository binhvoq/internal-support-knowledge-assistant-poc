using SupportPoc.KnowledgeService.Services;

namespace SupportPoc.KnowledgeService.Tests;

public sealed class DocumentIdGeneratorTests
{
    [Fact]
    public void Next_returns_DOC_001_when_empty()
    {
        Assert.Equal("DOC-001", DocumentIdGenerator.Next([]));
    }

    [Fact]
    public void Next_increments_from_highest_existing_id()
    {
        var ids = new[] { "DOC-002", "DOC-009" };
        Assert.Equal("DOC-010", DocumentIdGenerator.Next(ids));
    }
}
