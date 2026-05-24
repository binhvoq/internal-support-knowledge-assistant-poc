using System.Text.Json.Serialization;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace SupportPoc.KnowledgeService.Search;

public sealed class KnowledgeSearchDocument
{
    [SimpleField(IsKey = true)]
    [JsonPropertyName("documentId")]
    public required string DocumentId { get; set; }

    [SearchableField(IsFilterable = true)]
    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [SearchableField(IsFilterable = true)]
    [JsonPropertyName("category")]
    public required string Category { get; set; }

    [SearchableField]
    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [SimpleField]
    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }

    [SimpleField(IsSortable = true, IsFilterable = true)]
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [VectorSearchField(VectorSearchDimensions = 1536, VectorSearchProfileName = "vector-profile")]
    public IReadOnlyList<float>? Embedding { get; set; }
}
