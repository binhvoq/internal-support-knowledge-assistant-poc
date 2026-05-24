using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using SupportPoc.AiOrchestrator.Mcp;

namespace SupportPoc.AiOrchestrator.Plugins;

public sealed class McpKernelPlugin(McpToolGateway mcp)
{
    [KernelFunction("create_ticket")]
    [Description("Tao ticket ho tro moi qua MCP.")]
    public Task<string> CreateTicketAsync(
        [Description("Ma nhan vien")] string employeeId,
        [Description("Cau hoi ho tro")] string question,
        [Description("Category: IT, HR, Finance, Other")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        return mcp.CallToolAsync("create_ticket", new Dictionary<string, object?>
        {
            ["employeeId"] = employeeId,
            ["question"] = question,
            ["category"] = category
        }, cancellationToken);
    }

    [KernelFunction("get_ticket_status")]
    [Description("Lay trang thai ticket ho tro qua MCP tool get_ticket.")]
    public async Task<string> GetTicketStatusAsync(
        [Description("Ma ticket, vi du TCK-001")] string ticketId,
        CancellationToken cancellationToken = default)
    {
        var raw = await mcp.CallToolAsync("get_ticket", new Dictionary<string, object?> { ["ticketId"] = ticketId }, cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return JsonSerializer.Serialize(new
            {
                ticketId = root.TryGetProperty("id", out var id) ? id.GetString() : ticketId,
                status = root.TryGetProperty("status", out var st) ? st.GetString() : null,
                lastUpdatedAt = root.TryGetProperty("updatedAt", out var u) ? u.GetString() : null,
                hasAiSuggestion = root.TryGetProperty("hasAiSuggestion", out var h) && h.GetBoolean()
            });
        }
        catch
        {
            return raw;
        }
    }

    [KernelFunction("search_policy_documents")]
    [Description("Tim tai lieu noi bo qua MCP tool search_knowledge.")]
    public Task<string> SearchPolicyDocumentsAsync(
        [Description("Cau hoi hoac tu khoa")] string query,
        [Description("Category: IT, HR, Finance, Other")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["query"] = query };
        if (!string.IsNullOrWhiteSpace(category))
            args["category"] = category;
        return mcp.CallToolAsync("search_knowledge", args, cancellationToken);
    }

    [KernelFunction("update_ticket_status")]
    [Description("Cap nhat trang thai ticket qua MCP tool update_ticket_status.")]
    public Task<string> UpdateTicketStatusAsync(
        [Description("Ma ticket")] string ticketId,
        [Description("Trang thai moi")] string status,
        [Description("Cau tra loi cuoi khi resolve")] string? finalAnswer = null,
        CancellationToken cancellationToken = default)
    {
        return mcp.CallToolAsync("update_ticket_status", new Dictionary<string, object?>
        {
            ["ticketId"] = ticketId,
            ["status"] = status,
            ["finalAnswer"] = finalAnswer
        }, cancellationToken);
    }

    [KernelFunction("list_support_categories")]
    [Description("Lay danh sach category ho tro qua MCP.")]
    public Task<string> ListSupportCategoriesAsync(CancellationToken cancellationToken = default) =>
        mcp.CallToolAsync("list_support_categories", new Dictionary<string, object?>(), cancellationToken);
}
