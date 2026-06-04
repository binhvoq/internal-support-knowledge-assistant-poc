using System.Text;
using UglyToad.PdfPig;

namespace SupportPoc.KnowledgeService.Services;

public sealed class PdfKnowledgeExtractor
{
    public string ExtractText(Stream stream)
    {
        var builder = new StringBuilder();
        using var document = PdfDocument.Open(stream);
        foreach (var page in document.GetPages())
        {
            var text = page.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (builder.Length > 0)
                builder.AppendLine().AppendLine();
            builder.AppendLine(text);
        }

        return builder.ToString().Trim();
    }
}
