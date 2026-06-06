using System.Text.Json.Serialization;
using Azure.Search.Documents.Indexes;

namespace SupportPoc.KnowledgeService.Search;

public sealed class KnowledgeChunkSearchHit
{
    [SimpleField(IsKey = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.ChunkId)]
    public string? ChunkId { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.ParentId)]
    public string? ParentId { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.DocumentId)]
    public string? DocumentId { get; set; }

    [SearchableField]
    [JsonPropertyName(KnowledgeChunkIndexFields.Title)]
    public string? Title { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.Category)]
    public string? Category { get; set; }

    [SimpleField(IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.FileName)]
    public string? FileName { get; set; }

    [SimpleField]
    [JsonPropertyName(KnowledgeChunkIndexFields.SourceUrl)]
    public string? SourceUrl { get; set; }

    [SearchableField]
    [JsonPropertyName(KnowledgeChunkIndexFields.Content)]
    public string? Content { get; set; }

    [JsonPropertyName(KnowledgeChunkIndexFields.Embedding)]
    public IReadOnlyList<float>? Embedding { get; set; }

    [SimpleField(IsSortable = true, IsFilterable = true)]
    [JsonPropertyName(KnowledgeChunkIndexFields.UploadedAt)]
    public DateTimeOffset? UploadedAt { get; set; }
}
