using System.Text;
using SupportPoc.Shared.Models;

namespace SupportPoc.Shared.Formatting;

public static class KnowledgeChunkContextFormatter
{
    public static string Format(IReadOnlyList<RelatedDocument> related)
    {
        if (related.Count == 0)
            return "Khong tim thay chunk tai lieu lien quan trong knowledge base.";

        var context = new StringBuilder();
        foreach (var doc in related)
        {
            var chunkLabel = string.IsNullOrWhiteSpace(doc.ChunkId)
                ? $"[DOC {doc.DocumentId}]"
                : $"[CHUNK {doc.ChunkId}] DOC {doc.DocumentId}";

            context.Append(chunkLabel);
            context.Append($" | {doc.Title} (score {doc.Score:F2})");

            if (!string.IsNullOrWhiteSpace(doc.ParentId))
                context.Append($" | parent {doc.ParentId}");
            if (!string.IsNullOrWhiteSpace(doc.FileName))
                context.Append($" | file {doc.FileName}");

            context.AppendLine();
            if (!string.IsNullOrWhiteSpace(doc.Content))
                context.AppendLine(doc.Content.Trim());
            context.AppendLine();
        }

        return context.ToString();
    }
}
