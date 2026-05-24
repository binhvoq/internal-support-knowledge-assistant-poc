using System.Text.Json.Serialization;
using Azure.Search.Documents.Indexes;

namespace SupportPoc.KnowledgeService.Search;

/// <summary>Lightweight projection for search results (Select subset).</summary>
public sealed class KnowledgeSearchHit
{
    [SimpleField(IsKey = true)]
    [JsonPropertyName("documentId")]
    public string? DocumentId { get; set; }

    [SearchableField]
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [SearchableField]
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [SearchableField]
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
