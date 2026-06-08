using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;
using SupportPoc.Shared.Auth;

namespace SupportPoc.McpToolServer.Tools;

[McpServerToolType]
public sealed class SupportTools(IHttpClientFactory httpClientFactory)
{
    private HttpClient TicketClient => httpClientFactory.CreateClient("ticket-api");
    private HttpClient KnowledgeClient => httpClientFactory.CreateClient("knowledge-api");

    [McpServerTool(Name = "create_ticket", Destructive = false, Idempotent = false, OpenWorld = false)]
    [SupportToolPolicy(
        SupportToolRisks.Medium,
        AppRoleNames.Agent,
        Notes = "Creates a ticket through TicketService. Employee self-service should use direct API/UI flow, not this privileged MCP tool.")]
    [Description("Tao ticket ho tro moi.")]
    public async Task<string> CreateTicket(
        [Description("Ma nhan vien")] string employeeId,
        [Description("Cau hoi ho tro")] string question,
        [Description("Category: IT, HR, Finance, Other")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var response = await TicketClient.PostAsJsonAsync(
            "/tickets",
            new { employeeId, question, category },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Serialize(new { error = body.Length > 0 ? body : "Tao ticket that bai." });
        }
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [McpServerTool(Name = "get_ticket", ReadOnly = true, OpenWorld = false)]
    [SupportToolPolicy(
        SupportToolRisks.Medium,
        AppRoleNames.Agent,
        Notes = "Privileged ticket lookup. Employee-scoped reads need a separate get_my_ticket/user-context design.")]
    [Description("Lay chi tiet ticket ho tro theo ticketId.")]
    public async Task<string> GetTicket(
        [Description("Ma ticket, vi du TCK-001")] string ticketId,
        CancellationToken cancellationToken = default)
    {
        var response = await TicketClient.GetAsync($"/tickets/{ticketId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Serialize(new
            {
                error = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => $"Ticket {ticketId} khong ton tai.",
                    System.Net.HttpStatusCode.Unauthorized => "Ticket API tu choi — thieu Bearer (client credentials).",
                    _ => $"Ticket API {(int)response.StatusCode}: {(body.Length > 0 ? body : "loi khong ro")}",
                },
            });
        }
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [McpServerTool(Name = "update_ticket_status", Destructive = true, Idempotent = false, OpenWorld = false)]
    [SupportToolPolicy(
        SupportToolRisks.High,
        AppRoleNames.Agent,
        Notes = "Mutates ticket state. TicketService still owns resource-level authorization and state transition rules.")]
    [Description("Cap nhat trang thai ticket.")]
    public async Task<string> UpdateTicketStatus(
        [Description("Ma ticket")] string ticketId,
        [Description("Trang thai moi")] string status,
        [Description("Cau tra loi cuoi khi resolve")] string? finalAnswer = null,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        if (string.Equals(status, "Resolved", StringComparison.OrdinalIgnoreCase))
        {
            response = await TicketClient.PostAsJsonAsync(
                $"/tickets/{ticketId}/resolve",
                new { finalAnswer },
                cancellationToken);
        }
        else
        {
            response = await TicketClient.PatchAsJsonAsync(
                $"/tickets/{ticketId}",
                new { status, finalAnswer },
                cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
            return JsonSerializer.Serialize(new { error = $"Cap nhat {ticketId} that bai." });
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [McpServerTool(Name = "search_knowledge", ReadOnly = true, OpenWorld = false)]
    [SupportToolPolicy(
        SupportToolRisks.Low,
        AppRoleNames.Employee,
        AppRoleNames.Agent,
        AppRoleNames.KnowledgeAdmin,
        Notes = "Read-only knowledge search. Data authorization remains in KnowledgeService.")]
    [Description("Tim tai lieu noi bo theo query.")]
    public async Task<string> SearchKnowledge(
        [Description("Cau hoi tim kiem")] string query,
        [Description("Category: IT, HR, Finance, Other")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"/search?query={Uri.EscapeDataString(query)}&mode=hybrid&rerank=mmr";
        if (!string.IsNullOrWhiteSpace(category))
            url += $"&category={Uri.EscapeDataString(category)}";
        var response = await KnowledgeClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [McpServerTool(Name = "list_support_categories", ReadOnly = true, OpenWorld = false)]
    [SupportToolPolicy(
        SupportToolRisks.Low,
        AppRoleNames.Employee,
        AppRoleNames.Agent,
        AppRoleNames.KnowledgeAdmin,
        Notes = "Read-only category lookup.")]
    [Description("Danh sach category ho tro.")]
    public async Task<string> ListSupportCategories(CancellationToken cancellationToken = default)
    {
        var response = await KnowledgeClient.GetAsync("/categories", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
