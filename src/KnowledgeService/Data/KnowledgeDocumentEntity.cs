namespace SupportPoc.KnowledgeService.Data;

public sealed class KnowledgeDocumentEntity
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Category { get; set; }
    public required string Content { get; set; }
    public string? SourceUrl { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public string IngestionStatus { get; set; } = "Ready";
    public string? IngestionMessage { get; set; }
    public DateTimeOffset? IngestedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
