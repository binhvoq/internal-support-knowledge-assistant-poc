using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.Shared;
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
    private readonly ITicketSnapshotClient _ticketSnapshotClient;
    private readonly IChatCompletionService? _chat;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly ILogger<TicketSuggestionService> _logger;

    public TicketSuggestionService(
        AiPipelineService pipeline,
        McpToolGateway mcp,
        McpDynamicPluginLoader mcpLoader,
        McpToolAccessService toolAccess,
        Kernel kernel,
        ITicketSnapshotClient ticketSnapshotClient,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<TicketSuggestionService> logger)
    {
        _pipeline = pipeline;
        _mcp = mcp;
        _mcpLoader = mcpLoader;
        _toolAccess = toolAccess;
        _kernel = kernel;
        _ticketSnapshotClient = ticketSnapshotClient;
        _openAiOptions = openAiOptions.Value;
        _logger = logger;
        _chat = _openAiOptions.Enabled ? kernel.GetRequiredService<IChatCompletionService>() : null;
    }

    public async Task<string> ChatAsync(
        string message,
        IEnumerable<string>? roles = null,
        CancellationToken cancellationToken = default)
    {
        var ticketId = TicketIds.TryExtractFromText(message);
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            try
            {
                var snapshot = await _ticketSnapshotClient.GetTicketAsync(ticketId, cancellationToken);
                if (snapshot is not null)
                    return FormatTicketSnapshotReply(ticketId, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ticket snapshot lookup that bai cho {TicketId}; fallback offline.", ticketId);
            }
        }

        if (!_openAiOptions.Enabled || _chat is null)
            return await OfflineChatAsync(message, roles, cancellationToken);

        IReadOnlyList<RelatedDocument> related = [];
        try
        {
            related = await _pipeline.SearchKnowledgeAsync(message, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "KnowledgeService search that bai trong chat, tiep tuc voi context rong.");
        }

        McpToolCatalog? catalog = null;
        IReadOnlyList<KernelFunction> allowedFunctions = [];
        if (_mcpLoader.IsEnabled)
        {
            try
            {
                catalog = await _mcpLoader.LoadCatalogAsync(cancellationToken);
                allowedFunctions = await _toolAccess.GetAllowedFunctionsAsync(_kernel, roles, cancellationToken);
                _logger.LogInformation(
                    "Chat tool context ready: {ToolCount} tools, {AllowedCount} allowed.",
                    catalog.Tools.Count,
                    allowedFunctions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP tool context that bai trong chat, tiep tuc khong dung tool.");
            }
        }
        else
        {
            _logger.LogInformation("MCP tool server disabled by configuration; bo qua tool context trong chat.");
        }

        var knowledgeContext = BuildKnowledgeContext(related);
        try
        {
            var settings = new AzureOpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = allowedFunctions.Count > 0
                    ? FunctionChoiceBehavior.Auto(functions: allowedFunctions)
                    : null
            };

            var history = new ChatHistory();
            history.AddSystemMessage($"""
                Ban la tro ly ho tro noi bo. Chi tra loi dua tren chunk tai lieu tim duoc hoac ket qua MCP tool.
                Neu khong co chunk phu hop hoac khong du thong tin, noi ro can support agent xu ly. Khong tu bia chinh sach.
                Chunk tai lieu noi bo da tim truoc:
                {knowledgeContext}

                Cac MCP tool hien co (tu tools/list):
                {(catalog is null ? "Khong tai duoc MCP catalog." : catalog.DescribeForPrompt(allowedFunctions.Select(function => function.Name)))}
                Hay chon tool phu hop theo ten va schema tu server.
                """);
            history.AddUserMessage(message);

            var response = await _chat.GetChatMessageContentAsync(history, settings, _kernel, cancellationToken);
            return response.Content ?? "Khong co phan hoi tu AI.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Chat pipeline that bai; fallback offline. OpenAIEnabled={OpenAiEnabled} ChatConfigured={ChatConfigured} RelatedCount={RelatedCount} AllowedToolCount={AllowedToolCount}",
                _openAiOptions.Enabled,
                _openAiOptions.ChatConfigured,
                related.Count,
                allowedFunctions.Count);
            try
            {
                return await OfflineChatAsync(message, roles, cancellationToken, related);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Offline fallback chat that bai.");
                return "He thong AI tam thoi chua san sang. Vui long thu lai hoac mo ticket detail.";
            }
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
        CancellationToken cancellationToken,
        IReadOnlyList<RelatedDocument>? related = null)
    {
        var ticketId = TicketIds.TryExtractFromText(message);
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            if (!_mcpLoader.IsEnabled)
                return $"MCP tool server dang bi vo hieu hoa trong cloud. Khong the doc ticket {ticketId} bang tool, hay thu ticket detail hoac support queue.";

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

        if (related is { Count: > 0 })
        {
            var first = related[0];
            var preview = string.IsNullOrWhiteSpace(first.Content) ? first.Title : first.Content.Trim();
            if (preview.Length > 500)
                preview = preview[..500] + "...";

            return $"""
                [Fallback] Azure OpenAI tam thoi khong tra loi duoc, nhung da tim thay tai lieu noi bo lien quan.
                Tai lieu chinh: {first.Title}
                Noi dung lien quan: {preview}
                Hay thu lai Copilot sau hoac mo ticket detail de support agent doi chieu.
                """;
        }

        return $"Azure OpenAI chua san sang. Fallback local chi ho tro hoi trang thai ticket khi message chua ID 32 ky tu hex (vi du {TicketIds.Example}).";
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

    private static string FormatTicketSnapshotReply(string requestedTicketId, TicketSnapshot snapshot)
    {
        var suggestionText = snapshot.HasAiSuggestion
            ? "AI da tao goi y tra loi va dang cho agent xac nhan."
            : "AI chua co goi y tra loi.";
        var finalText = snapshot.HasFinalAnswer
            ? "Ticket da co cau tra loi cuoi cung."
            : "Ticket chua co cau tra loi cuoi cung.";

        return $"Ticket {requestedTicketId} hien dang o trang thai {snapshot.Status}. {suggestionText} {finalText} Ticket noi bo: {snapshot.TicketId}, version {snapshot.Version}.";
    }
}
