namespace SupportPoc.Shared.Models;

public sealed class KnowledgeDocumentDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Content { get; init; }
    public string? SourceUrl { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public string IngestionStatus { get; init; } = "Ready";
    public string? IngestionMessage { get; init; }
    public DateTimeOffset? IngestedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
