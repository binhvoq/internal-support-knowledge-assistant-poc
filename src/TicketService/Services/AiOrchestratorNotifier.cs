namespace SupportPoc.TicketService.Services;

public sealed class AiOrchestratorNotifier(HttpClient http, IConfiguration configuration, ILogger<AiOrchestratorNotifier> logger)
{
    public async Task NotifyTicketCreatedAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["Services:AiOrchestrator"] ?? "http://localhost:5003";
        try
        {
            var response = await http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/internal/ticket-created",
                new { ticketId },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("AI Orchestrator notify failed: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Khong goi duoc AI Orchestrator — ticket van duoc tao.");
        }
    }
}
