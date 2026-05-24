namespace SupportPoc.Shared.Models;

public sealed class KnowledgeDocumentDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Content { get; init; }
    public string? SourceUrl { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
