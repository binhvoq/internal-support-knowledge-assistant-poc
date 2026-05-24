using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using SupportPoc.AiOrchestrator.Clients;
using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Plugins;
using SupportPoc.Shared.Events;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class TicketSuggestionService
{
    private readonly TicketApiClient _tickets;
    private readonly McpToolGateway _mcp;
    private readonly ISupportEventPublisher _publisher;
    private readonly Kernel _kernel;
    private readonly IChatCompletionService? _chat;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly ILogger<TicketSuggestionService> _logger;

    public TicketSuggestionService(
        TicketApiClient tickets,
        McpToolGateway mcp,
        ISupportEventPublisher publisher,
        Kernel kernel,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<TicketSuggestionService> logger)
    {
        _tickets = tickets;
        _mcp = mcp;
        _publisher = publisher;
        _kernel = kernel;
        _openAiOptions = openAiOptions.Value;
        _logger = logger;
        _chat = _openAiOptions.Enabled ? kernel.GetRequiredService<IChatCompletionService>() : null;

        _kernel.Plugins.AddFromObject(new McpKernelPlugin(mcp), "Mcp");
    }

    public async Task ProcessTicketCreatedAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await _tickets.GetAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            _logger.LogWarning("Ticket {TicketId} khong ton tai.", ticketId);
            return;
        }

        if (ticket.Status is TicketStatus.Suggested or TicketStatus.Resolved
            && !string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
        {
            _logger.LogInformation("Ticket {TicketId} da co AI suggestion — bo qua xu ly trung.", ticketId);
            return;
        }

        if (ticket.Status is not TicketStatus.Analyzing)
            await _tickets.PatchAsync(ticketId, new { status = TicketStatus.Analyzing }, cancellationToken);

        var category = ticket.Category;
        if (category is SupportCategory.Other or "")
            category = await ClassifyCategoryAsync(ticket.Question, cancellationToken) ?? SupportCategory.Other;

        var related = await SearchKnowledgeViaMcpAsync(ticket.Question, category, cancellationToken);
        var suggestion = await GenerateSuggestionAsync(ticket.Question, related, cancellationToken);

        await _tickets.PatchAsync(ticketId, new
        {
            status = TicketStatus.Suggested,
            category,
            aiSuggestedAnswer = suggestion,
            relatedDocuments = related
        }, cancellationToken);

        try
        {
            await _publisher.PublishAsync(SupportEventTypes.AiSuggestionGenerated, new AiSuggestionGeneratedPayload { TicketId = ticketId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publish event {EventType} that bai; suggestion da duoc luu.", SupportEventTypes.AiSuggestionGenerated);
        }
    }

    public async Task<string> ChatAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!_openAiOptions.Enabled || _chat is null)
            return await OfflineChatAsync(message, cancellationToken);

        var settings = new AzureOpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        var history = new ChatHistory();
        history.AddSystemMessage("""
            Ban la tro ly ho tro noi bo. Chi tra loi dua tren tai lieu tim duoc hoac ket qua MCP tool.
            Neu khong du thong tin, noi ro can support agent xu ly. Khong tu bia chinh sach.
            Khi user muon tao ticket, goi create_ticket (can employeeId va question).
            Khi user hoi trang thai ticket, goi get_ticket_status. Khi can tim tai lieu, goi search_policy_documents.
            Khi resolve ticket, goi update_ticket_status voi status Resolved va finalAnswer.
            """);
        history.AddUserMessage(message);

        try
        {
            var response = await _chat.GetChatMessageContentAsync(history, settings, _kernel, cancellationToken);
            return response.Content ?? "Khong co phan hoi tu AI.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI chat that bai; dung MCP fallback.");
            return await OfflineChatAsync(message, cancellationToken);
        }
    }

    public async Task<string> SuggestAnswerAsync(string question, string? category, CancellationToken cancellationToken = default)
    {
        var related = await SearchKnowledgeViaMcpAsync(question, category ?? SupportCategory.Other, cancellationToken);
        return await GenerateSuggestionAsync(question, related, cancellationToken);
    }

    public async Task<ClassificationResult?> ClassifyTicketAsync(string question, CancellationToken cancellationToken = default)
    {
        var category = await ClassifyCategoryAsync(question, cancellationToken);
        return category is null ? null : new ClassificationResult(category, 0.8, "LLM classification");
    }

    private async Task<IReadOnlyList<RelatedDocument>> SearchKnowledgeViaMcpAsync(
        string query, string? category, CancellationToken cancellationToken)
    {
        try
        {
            var args = new Dictionary<string, object?> { ["query"] = query };
            if (!string.IsNullOrWhiteSpace(category) && category != SupportCategory.Other)
                args["category"] = category;

            var json = await _mcp.CallToolAsync("search_knowledge", args, cancellationToken);
            return McpKnowledgeParser.ParseSearchResults(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP search_knowledge that bai.");
            return [];
        }
    }

    private async Task<string?> ClassifyCategoryAsync(string question, CancellationToken cancellationToken)
    {
        if (!_openAiOptions.Enabled || _chat is null) return SupportCategory.Other;

        var prompt = """
            Phan loai cau hoi ho tro vao IT, HR, Finance hoac Other.
            Chi tra ve JSON: {"category":"IT","confidence":0.9,"reason":"..."}
            Cau hoi: 
            """ + question;

        ChatMessageContent result;
        try
        {
            result = await _chat.GetChatMessageContentAsync(prompt, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI classify that bai; fallback Other.");
            return SupportCategory.Other;
        }
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<ClassificationResult>(result.Content ?? "{}");
            if (parsed?.Confidence < 0.5) return SupportCategory.Other;
            return parsed?.Category ?? SupportCategory.Other;
        }
        catch
        {
            return SupportCategory.Other;
        }
    }

    private async Task<string> GenerateSuggestionAsync(string question, IReadOnlyList<RelatedDocument> related, CancellationToken cancellationToken)
    {
        if (!_openAiOptions.Enabled || _chat is null)
        {
            return BuildOfflineSuggestion(related);
        }

        var context = new StringBuilder();
        if (related.Count == 0)
        {
            context.AppendLine("Khong tim thay tai lieu lien quan trong knowledge base.");
        }
        else
        {
            foreach (var doc in related)
            {
                context.AppendLine($"[DOC {doc.DocumentId}] {doc.Title} (score {doc.Score:F2})");
                if (!string.IsNullOrWhiteSpace(doc.Content))
                    context.AppendLine(doc.Content.Trim());
                context.AppendLine();
            }
        }

        var prompt = $"""
            Ban la tro ly ho tro noi bo. Viet cau tra loi goi y cho support agent.
            QUY TAC:
            - Chi dung thong tin tu phan "Tai lieu noi bo" ben duoi.
            - Neu co buoc xu ly trong tai lieu, liet ke ro rang (Buoc 1, Buoc 2...).
            - Khong tu them chinh sach/buoc khong co trong tai lieu.
            - Neu tai lieu khong du, noi ro can agent xu ly thu cong.

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
            _logger.LogWarning(ex, "Azure OpenAI suggestion that bai; dung fallback tu related docs.");
            return BuildOfflineSuggestion(related);
        }
    }

    private async Task<string> OfflineChatAsync(string message, CancellationToken cancellationToken)
    {
        var ticketId = Regex.Match(message, @"TCK-\d+", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            var raw = await _mcp.CallToolAsync("get_ticket", new Dictionary<string, object?> { ["ticketId"] = ticketId }, cancellationToken);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "Unknown";
                var updatedAt = root.TryGetProperty("updatedAt", out var u) ? u.GetString() : null;
                return $"Ticket {ticketId} dang o trang thai {status}. Cap nhat lan cuoi: {updatedAt ?? "khong ro"}.";
            }
            catch
            {
                return raw;
            }
        }

        return "Azure OpenAI chua san sang. Fallback local chi ho tro hoi trang thai ticket theo ma TCK-xxx.";
    }

    private static string BuildOfflineSuggestion(IReadOnlyList<RelatedDocument> related)
    {
        if (related.Count == 0)
            return "Chua goi duoc Azure OpenAI. Khong du thong tin de goi y - can support agent xu ly.";

        var first = related[0];
        var basis = string.IsNullOrWhiteSpace(first.Content)
            ? first.Title
            : first.Content.Trim();
        if (basis.Length > 700)
            basis = basis[..700] + "...";

        return $"""
            [Offline] Tim thay {related.Count} tai lieu lien quan. Azure OpenAI chua san sang nen day la goi y fallback cho agent kiem tra:

            Tai lieu chinh: {first.Title}
            Noi dung lien quan: {basis}

            Agent nen doi chieu tai lieu noi bo truoc khi resolve ticket.
            """;
    }
}

public sealed record ClassificationResult(string Category, double Confidence, string Reason);
