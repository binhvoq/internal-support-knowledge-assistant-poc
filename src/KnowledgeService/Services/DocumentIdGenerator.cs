using SupportPoc.KnowledgeService.Data;

namespace SupportPoc.KnowledgeService.Services;

internal static class DocumentIdGenerator
{
    public static string Next(IEnumerable<string> existingIds)
    {
        var max = existingIds
            .Select(id => int.TryParse(id.Replace("DOC-", "", StringComparison.OrdinalIgnoreCase), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"DOC-{(max + 1):D3}";
    }
}
