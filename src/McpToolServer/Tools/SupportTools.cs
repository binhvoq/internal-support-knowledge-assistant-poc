using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SupportPoc.McpToolServer.Tools;

[McpServerToolType]
public sealed class SupportTools(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    private HttpClient TicketClient => CreateClient("Services:TicketService", "http://localhost:5001");
    private HttpClient KnowledgeClient => CreateClient("Services:KnowledgeService", "http://localhost:5002");

    private HttpClient CreateClient(string configKey, string fallback)
    {
        var client = httpClientFactory.CreateClient();
        var baseUrl = configuration[configKey] ?? fallback;
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        return client;
    }

    [McpServerTool]
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

    [McpServerTool]
    [Description("Lay chi tiet ticket ho tro theo ticketId.")]
    public async Task<string> GetTicket(
        [Description("Ma ticket, vi du TCK-001")] string ticketId,
        CancellationToken cancellationToken = default)
    {
        var response = await TicketClient.GetAsync($"/tickets/{ticketId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return JsonSerializer.Serialize(new { error = $"Ticket {ticketId} khong ton tai." });
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [McpServerTool]
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

    [McpServerTool]
    [Description("Tim tai lieu noi bo theo query.")]
    public async Task<string> SearchKnowledge(
        [Description("Cau hoi tim kiem")] string query,
        [Description("Category: IT, HR, Finance, Other")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"/search?query={Uri.EscapeDataString(query)}&mode=hybrid";
        if (!string.IsNullOrWhiteSpace(category))
            url += $"&category={Uri.EscapeDataString(category)}";
        var response = await KnowledgeClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [McpServerTool]
    [Description("Danh sach category ho tro.")]
    public async Task<string> ListSupportCategories(CancellationToken cancellationToken = default)
    {
        var response = await KnowledgeClient.GetAsync("/categories", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
