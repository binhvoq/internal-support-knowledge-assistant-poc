using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Formatting;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;

namespace SupportPoc.AiOrchestrator.Services;

public interface IAiPipelineService
{
    Task<AiPipelineService.PipelineResult> RunAsync(
        string question,
        string requestedCategory,
        CancellationToken cancellationToken);
}

// Classify + Search + Generate — dung boi AiGenerationWorkerService (durable background job).
public sealed class AiPipelineService : IAiPipelineService
{
    private readonly IKnowledgeSearchClient _knowledge;
    private readonly IChatCompletionService? _chat;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly ILogger<AiPipelineService> _logger;

    public AiPipelineService(
        IKnowledgeSearchClient knowledge,
        IChatCompletionServiceAccessor chatAccessor,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<AiPipelineService> logger)
    {
        _knowledge = knowledge;
        _chat = chatAccessor.Chat;
        _openAiOptions = openAiOptions.Value;
        _logger = logger;
    }

    public sealed record PipelineResult(string Category, string Suggestion, IReadOnlyList<RelatedDocument> Related);

    public async Task<PipelineResult> RunAsync(
        string question,
        string requestedCategory,
        CancellationToken cancellationToken)
    {
        if (question.Has(FaultInjection.ForceAiFail))
        {
            _logger.LogWarning("FaultInjection: ForceAiFail marker detected -> throwing simulated AI failure.");
            throw new InvalidOperationException("Simulated AI pipeline failure (fault injection).");
        }

        var category = requestedCategory;
        if (string.IsNullOrWhiteSpace(category) || category == SupportCategory.Other)
            category = await ClassifyAsync(question, cancellationToken) ?? SupportCategory.Other;

        var related = await SearchKnowledgeAsync(question, null, cancellationToken);
        var suggestion = await GenerateAsync(question, related, cancellationToken);
        return new PipelineResult(category, suggestion, related);
    }

    public async Task<string?> ClassifyAsync(string question, CancellationToken cancellationToken)
    {
        var keyword = TryKeywordClassify(question);
        if (!_openAiOptions.Enabled || _chat is null)
            return keyword;

        var prompt = """
            Phan loai cau hoi ho tro nhan vien vao mot trong: IT, HR, Finance, Other.
            - IT: VPN, mat khau, phan mem, may tinh, email cong ty
            - HR: nghi phep, luong, hop dong lao dong, phuc loi
            - Finance: reimburse, chi phi, hoa don, thanh toan
            - Other: khong ro

            Chi tra ve JSON hop le, khong markdown:
            {"category":"HR","confidence":0.9,"reason":"..."}

            Cau hoi:
            """ + question;

        try
        {
            var result = await _chat.GetChatMessageContentAsync(prompt, cancellationToken: cancellationToken);
            var parsed = TryParseClassification(result.Content);
            if (parsed is null || parsed.Confidence < 0.5)
                return keyword;
            if (parsed.Category == SupportCategory.Other && keyword is not null)
                return keyword;
            return parsed.Category;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Classify LLM that bai - fallback keyword.");
            return keyword;
        }
    }

    public async Task<IReadOnlyList<RelatedDocument>> SearchKnowledgeAsync(string query, string? category, CancellationToken cancellationToken)
    {
        try
        {
            return await _knowledge.SearchAsync(query, category, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KnowledgeService search that bai.");
            return [];
        }
    }

    public async Task<string> GenerateAsync(string question, IReadOnlyList<RelatedDocument> related, CancellationToken cancellationToken)
    {
        if (!_openAiOptions.Enabled || _chat is null)
            return BuildOfflineSuggestion(related);

        var context = KnowledgeChunkContextFormatter.Format(related);

        var prompt = $"""
            Ban la tro ly ho tro noi bo. Viet cau tra loi goi y cho support agent.
            QUY TAC:
            - Chi dung thong tin tu cac chunk tai lieu noi bo ben duoi.
            - Neu co buoc xu ly trong chunk, liet ke ro rang (Buoc 1, Buoc 2...).
            - Khong tu them chinh sach/buoc khong co trong chunk.
            - Neu khong co chunk phu hop hoac thong tin khong du, noi ro can agent xu ly thu cong.

            Cau hoi nhan vien: {question}

            Tai lieu noi bo:
            {context}
            """;

        try
        {
            var response = await _chat.GetChatMessageContentAsync(prompt, cancellationToken: cancellationToken);
            return response.Content ?? "Khong tao duoc goi y.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI suggestion that bai - dung fallback tu related docs.");
            return BuildOfflineSuggestion(related);
        }
    }

    private static string BuildOfflineSuggestion(IReadOnlyList<RelatedDocument> related)
    {
        if (related.Count == 0)
            return "Chua goi duoc Azure OpenAI. Khong du thong tin de goi y - can support agent xu ly.";

        var first = related[0];
        var basis = string.IsNullOrWhiteSpace(first.Content) ? first.Title : first.Content.Trim();
        if (basis.Length > 700)
            basis = basis[..700] + "...";

        return $"""
            [Offline] Tim thay {related.Count} tai lieu lien quan. Azure OpenAI chua san sang nen day la goi y fallback cho agent kiem tra:

            Tai lieu chinh: {first.Title}
            Noi dung lien quan: {basis}

            Agent nen doi chieu tai lieu noi bo truoc khi resolve ticket.
            """;
    }

    private static string? TryKeywordClassify(string question)
    {
        var q = question.ToLowerInvariant();
        if (q.Contains("vpn") || q.Contains("mat khau") || q.Contains("phan mem") || q.Contains("may tinh") || q.Contains("email cong ty"))
            return SupportCategory.IT;
        if (q.Contains("nghi phep") || q.Contains("luong") || q.Contains("hop dong") || q.Contains("phuc loi") || q.Contains("onboarding"))
            return SupportCategory.HR;
        if (q.Contains("reimburse") || q.Contains("chi phi") || q.Contains("hoa don") || q.Contains("thanh toan"))
            return SupportCategory.Finance;
        return null;
    }

    private static ClassificationResult? TryParseClassification(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var json = content.Trim();
        var match = Regex.Match(json, @"\{[\s\S]*\}");
        if (match.Success) json = match.Value;
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<ClassificationResult>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Category)) return null;
            var normalized = NormalizeCategory(parsed.Category);
            return normalized is null ? null : parsed with { Category = normalized };
        }
        catch { return null; }
    }

    private static string? NormalizeCategory(string raw)
    {
        var trimmed = raw.Trim();
        foreach (var cat in SupportCategory.All)
            if (string.Equals(trimmed, cat, StringComparison.OrdinalIgnoreCase))
                return cat;
        return trimmed.ToUpperInvariant() switch
        {
            "IT" => SupportCategory.IT,
            "HR" => SupportCategory.HR,
            "FINANCE" => SupportCategory.Finance,
            "OTHER" => SupportCategory.Other,
            _ => null
        };
    }
}

public sealed record ClassificationResult(string Category, double Confidence, string Reason);

// Wrapper de DI khong fail khi Azure OpenAI chua bat (IChatCompletionService chua duoc register).
public sealed class IChatCompletionServiceAccessor
{
    public IChatCompletionService? Chat { get; }
    public IChatCompletionServiceAccessor(IServiceProvider sp, IOptions<AzureOpenAIOptions> opts)
    {
        Chat = opts.Value.Enabled ? sp.GetService<IChatCompletionService>() : null;
    }
}
