using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Messaging;

namespace SupportPoc.TicketService.Services;

public sealed class OrchestratorDevBridge(
    IHttpClientFactory httpClientFactory,
    IOptions<LocalMessagingOptions> options,
    ILogger<OrchestratorDevBridge> logger)
{
    public const string HttpClientName = "ai-orchestrator-bridge";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    /// <summary>Best-effort debug shortcut — khong co Outbox. Retry ngan; false neu van fail sau khi ticket da commit.</summary>
    public async Task<bool> TryNotifyTicketCreatedAsync(ITicketCreated message, CancellationToken cancellationToken = default)
    {
        var baseUrl = options.Value.AiOrchestratorBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{baseUrl}/internal/dev/ticket-created";

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                var response = await client.PostAsJsonAsync(url, message, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    if (attempt > 0)
                    {
                        logger.LogWarning(
                            "HTTP bridge ticket-created succeeded after retry {Attempt} TicketId={TicketId} JobId={JobId}",
                            attempt,
                            message.TicketId,
                            message.JobId);
                    }
                    return true;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "HTTP bridge ticket-created attempt {Attempt} failed Status={Status} TicketId={TicketId} JobId={JobId} Body={Body}",
                    attempt + 1,
                    (int)response.StatusCode,
                    message.TicketId,
                    message.JobId,
                    body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "HTTP bridge ticket-created attempt {Attempt} threw TicketId={TicketId} JobId={JobId}",
                    attempt + 1,
                    message.TicketId,
                    message.JobId);
            }

            if (attempt < RetryDelays.Length)
                await Task.Delay(RetryDelays[attempt], cancellationToken);
        }

        logger.LogError(
            "HTTP bridge ticket-created EXHAUSTED retries — ticket saved but auto-suggestion job NOT enqueued. TicketId={TicketId} JobId={JobId}",
            message.TicketId,
            message.JobId);
        return false;
    }
}
