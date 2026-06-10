using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Formatting;
using SupportPoc.Shared.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Services;

// HTTP /ai/* only — auto suggestion orchestration is TicketSuggestionStateMachine saga (not this class).
public sealed class TicketSuggestionService
{
    private readonly AiPipelineService _pipeline;
    private readonly McpToolGateway _mcp;
    private readonly McpDynamicPluginLoader _mcpLoader;
    private readonly McpToolAccessService _toolAccess;
    private readonly Kernel _kernel;
    private readonly IChatCompletionService? _chat;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly ILogger<TicketSuggestionService> _logger;

    public TicketSuggestionService(
        AiPipelineService pipeline,
        McpToolGateway mcp,
        McpDynamicPluginLoader mcpLoader,
        McpToolAccessService toolAccess,
        Kernel kernel,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<TicketSuggestionService> logger)
    {
        _pipeline = pipeline;
        _mcp = mcp;
        _mcpLoader = mcpLoader;
        _toolAccess = toolAccess;
        _kernel = kernel;
        _openAiOptions = openAiOptions.Value;
        _logger = logger;
        _chat = _openAiOptions.Enabled ? kernel.GetRequiredService<IChatCompletionService>() : null;
    }

    public async Task<string> ChatAsync(
        string message,
        IEnumerable<string>? roles = null,
        CancellationToken cancellationToken = default)
    {
        if (!_openAiOptions.Enabled || _chat is null)
            return await OfflineChatAsync(message, roles, cancellationToken);

        var catalog = await _mcpLoader.LoadCatalogAsync(cancellationToken);
        var allowedFunctions = await _toolAccess.GetAllowedFunctionsAsync(_kernel, roles, cancellationToken);
        var related = await _pipeline.SearchKnowledgeAsync(message, null, cancellationToken);
        var knowledgeContext = BuildKnowledgeContext(related);
        var settings = new AzureOpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(functions: allowedFunctions)
        };

        var history = new ChatHistory();
        history.AddSystemMessage($"""
            Ban la tro ly ho tro noi bo. Chi tra loi dua tren chunk tai lieu tim duoc hoac ket qua MCP tool.
            Neu khong co chunk phu hop hoac khong du thong tin, noi ro can support agent xu ly. Khong tu bia chinh sach.
            Chunk tai lieu noi bo da tim truoc:
            {knowledgeContext}

            Cac MCP tool hien co (tu tools/list):
            {catalog.DescribeForPrompt(allowedFunctions.Select(function => function.Name))}
            Hay chon tool phu hop theo ten va schema tu server.
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
            return await OfflineChatAsync(message, roles, cancellationToken);
        }
    }

    private static string BuildKnowledgeContext(IReadOnlyList<RelatedDocument> related) =>
        KnowledgeChunkContextFormatter.Format(related);

    public async Task<string> SuggestAnswerAsync(string question, string? category, CancellationToken cancellationToken = default)
    {
        var related = await _pipeline.SearchKnowledgeAsync(question, category, cancellationToken);
        return await _pipeline.GenerateAsync(question, related, cancellationToken);
    }

    public async Task<ClassificationResult?> ClassifyTicketAsync(string question, CancellationToken cancellationToken = default)
    {
        var cat = await _pipeline.ClassifyAsync(question, cancellationToken);
        return cat is null ? null : new ClassificationResult(cat, 0.8, "Pipeline classify");
    }

    private async Task<string> OfflineChatAsync(
        string message,
        IEnumerable<string>? roles,
        CancellationToken cancellationToken)
    {
        var ticketId = Regex.Match(message, @"TCK-\d+", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            var catalog = await _mcpLoader.LoadCatalogAsync(cancellationToken);
            var toolName = catalog.Require("get_ticket");
            var allowedTools = await _toolAccess.GetAllowedToolNamesAsync(roles, cancellationToken);
            if (!allowedTools.Contains(toolName))
                return $"Tai khoan hien tai khong duoc dung MCP tool '{toolName}' de doc ticket. Can Agent hoac tool Employee-scoped rieng.";
            var raw = await _mcp.CallToolAsync(
                toolName,
                new Dictionary<string, object?> { ["ticketId"] = ticketId },
                new McpCallContext(McpCallContext.SourceOfflineChat, TicketId: ticketId),
                cancellationToken);
            if (TryFormatTicketStatusReply(ticketId, raw, out var formatted))
                return formatted;
            return raw;
        }

        return "Azure OpenAI chua san sang. Fallback local chi ho tro hoi trang thai ticket theo ma TCK-xxx.";
    }

    private static readonly JsonSerializerOptions TicketJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static bool TryFormatTicketStatusReply(string ticketId, string raw, out string reply)
    {
        reply = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                reply = $"Khong lay duoc ticket {ticketId}: {err.GetString()}";
                return true;
            }

            var dto = JsonSerializer.Deserialize<TicketDto>(raw, TicketJsonOptions);
            if (dto is not null && !string.IsNullOrWhiteSpace(dto.Status))
            {
                reply = $"Ticket {ticketId} dang o trang thai {dto.Status}. Cap nhat lan cuoi: {dto.UpdatedAt:yyyy-MM-dd HH:mm} UTC.";
                return true;
            }
        }
        catch
        {
            // fall through
        }

        reply = "";
        return false;
    }
}
