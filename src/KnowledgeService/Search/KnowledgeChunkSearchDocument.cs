using System.Text.Json.Serialization;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace SupportPoc.KnowledgeService.Search;

/// <summary>
/// Projection for delete/partial document operations.
/// Vector field dimensions are defined in <see cref="KnowledgeSearchService.EnsureChunkIndexAsync"/> from config.
/// </summary>
public sealed class KnowledgeChunkSearchDocument
{
    [SimpleField(IsKey = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.ChunkId)]
    public required string ChunkId { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.ParentId)]
    public string? ParentId { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.DocumentId)]
    public required string DocumentId { get; set; }

    [SearchableField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.Title)]
    public required string Title { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.Category)]
    public required string Category { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.FileName)]
    public string? FileName { get; set; }

    [SimpleField]
    [JsonPropertyName(KnowledgeChunkIndexFields.SourceUrl)]
    public string? SourceUrl { get; set; }

    [SearchableField]
    [JsonPropertyName(KnowledgeChunkIndexFields.Content)]
    public required string Content { get; set; }

    [SimpleField(IsSortable = true, IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.UploadedAt)]
    public DateTimeOffset UploadedAt { get; set; }
}
