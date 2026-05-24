using System.Text.Json;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Mcp;

internal static class McpKnowledgeParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<RelatedDocument> ParseSearchResults(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return [];

            var list = new List<RelatedDocument>();
            foreach (var item in results.EnumerateArray())
            {
                var id = item.TryGetProperty("documentId", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new RelatedDocument
                {
                    DocumentId = id,
                    Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? id : id,
                    Content = item.TryGetProperty("content", out var c) ? c.GetString() : null,
                    Score = item.TryGetProperty("score", out var s) && s.TryGetDouble(out var score) ? score : 0
                });
            }
            return list;
        }
        catch
        {
            return [];
        }
    }
}
