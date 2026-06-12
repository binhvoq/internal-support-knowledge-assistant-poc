using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class HttpTicketSuggestionReconcileClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LocalMessagingOptions> localOptions,
    IOptions<AutoSuggestionOptions> autoSuggestionOptions) : ITicketSuggestionReconcileClient
{
    public async Task<AutoSuggestionReconcileResult> ReconcileAsync(
        string ticketId,
        Guid jobId,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var opts = autoSuggestionOptions.Value;
        var maxRetries = Math.Max(0, opts.ReconcileHttpMaxRetries);
        var timeout = TimeSpan.FromSeconds(Math.Max(3, opts.ReconcileHttpTimeoutSeconds));
        Exception? lastError = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delayMs = 250 * attempt;
                await Task.Delay(delayMs, cancellationToken);
            }

            try
            {
                return await SendOnceAsync(ticketId, jobId, expectedVersion, timeout, cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxRetries)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new HttpRequestException("TicketService reconcile failed after retries.");
    }

    private async Task<AutoSuggestionReconcileResult> SendOnceAsync(
        string ticketId,
        Guid jobId,
        long? expectedVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var baseUrl = localOptions.Value.TicketServiceBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient(HttpTicketSnapshotClient.HttpClientName);

        var query = $"jobId={jobId}";
        if (expectedVersion is not null)
            query += $"&expectedVersion={expectedVersion.Value}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var response = await client.GetAsync(
            $"{baseUrl}/tickets/{ticketId}/auto-suggestion-reconcile?{query}",
            timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            if (IsTransient(response.StatusCode))
                throw new HttpRequestException($"TicketService reconcile transient failure: {(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<AutoSuggestionReconcileResult>(
            cancellationToken: timeoutCts.Token);

        return result ?? throw new InvalidOperationException(
            $"TicketService reconcile returned empty body for ticket {ticketId}.");
    }

    internal static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException
        || (ex is InvalidOperationException && ex.InnerException is HttpRequestException);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
