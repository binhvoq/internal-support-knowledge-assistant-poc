using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Services;

// Sau khi chuyen sang MassTransit saga, service nay CHI con ho tro cac HTTP endpoint
// (chat / suggest-answer / classify-ticket) - khong con orchestration logic nua.
// Toan bo orchestration nam o TicketSuggestionStateMachine.
public sealed class TicketSuggestionService
{
    private readonly AiPipelineService _pipeline;
    private readonly McpToolGateway _mcp;
    private readonly McpDynamicPluginLoader _mcpLoader;
    private readonly Kernel _kernel;
    private readonly IChatCompletionService? _chat;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly ILogger<TicketSuggestionService> _logger;

    public TicketSuggestionService(
        AiPipelineService pipeline,
        McpToolGateway mcp,
        McpDynamicPluginLoader mcpLoader,
        Kernel kernel,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<TicketSuggestionService> logger)
    {
        _pipeline = pipeline;
        _mcp = mcp;
        _mcpLoader = mcpLoader;
        _kernel = kernel;
        _openAiOptions = openAiOptions.Value;
        _logger = logger;
        _chat = _openAiOptions.Enabled ? kernel.GetRequiredService<IChatCompletionService>() : null;
    }

    public async Task<string> ChatAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!_openAiOptions.Enabled || _chat is null)
            return await OfflineChatAsync(message, cancellationToken);

        var catalog = await _mcpLoader.LoadCatalogAsync(cancellationToken);
        await _mcpLoader.RegisterWithKernelAsync(_kernel, cancellationToken);
        var settings = new AzureOpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        var history = new ChatHistory();
        history.AddSystemMessage($"""
            Ban la tro ly ho tro noi bo. Chi tra loi dua tren tai lieu tim duoc hoac ket qua MCP tool.
            Neu khong du thong tin, noi ro can support agent xu ly. Khong tu bia chinh sach.
            Cac MCP tool hien co (tu tools/list):
            {catalog.DescribeForPrompt()}
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
            return await OfflineChatAsync(message, cancellationToken);
        }
    }

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

    private async Task<string> OfflineChatAsync(string message, CancellationToken cancellationToken)
    {
        var ticketId = Regex.Match(message, @"TCK-\d+", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            var catalog = await _mcpLoader.LoadCatalogAsync(cancellationToken);
            var toolName = catalog.Require("get_ticket");
            var raw = await _mcp.CallToolAsync(toolName, new Dictionary<string, object?> { ["ticketId"] = ticketId }, cancellationToken);
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
}
