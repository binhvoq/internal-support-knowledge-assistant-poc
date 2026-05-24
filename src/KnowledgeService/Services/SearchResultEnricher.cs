using Microsoft.EntityFrameworkCore;
using SupportPoc.KnowledgeService.Data;
using SupportPoc.Shared.Models;

namespace SupportPoc.KnowledgeService.Services;

internal static class SearchResultEnricher
{
    public static async Task<IReadOnlyList<RelatedDocument>> WithContentAsync(
        KnowledgeDbContext db,
        IReadOnlyList<RelatedDocument> hits,
        CancellationToken cancellationToken = default)
    {
        if (hits.Count == 0) return hits;

        var ids = hits.Select(h => h.DocumentId).ToList();
        var contents = await db.Documents
            .Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Content, cancellationToken);

        return hits.Select(h => new RelatedDocument
        {
            DocumentId = h.DocumentId,
            Title = h.Title,
            Content = contents.TryGetValue(h.DocumentId, out var c) ? c : h.Content,
            Score = h.Score
        }).ToList();
    }
}
