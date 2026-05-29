using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;

public sealed class HttpTicketProgressProbe : ITicketProgressProbe
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpTicketProgressProbe> _logger;

    public HttpTicketProgressProbe(
        HttpClient http,
        IOptions<ServiceEndpointsOptions> endpoints,
        ILogger<HttpTicketProgressProbe> logger)
    {
        _http = http;
        _logger = logger;
        var baseUrl = endpoints.Value.TicketService.TrimEnd('/');
        _http.BaseAddress = new Uri(baseUrl + "/");
    }

    public async Task<TicketProgressProbeResult> GetAsync(string ticketId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetAsync(
                $"internal/tickets/{Uri.EscapeDataString(ticketId)}/saga-progress",
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(TicketProgressProbeStatus.NotFound, null, "Ticket not found");

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    TicketProgressProbeStatus.Unavailable,
                    null,
                    $"HTTP {(int)response.StatusCode}");
            }

            var dto = await response.Content.ReadFromJsonAsync<TicketSagaProgressResponse>(cancellationToken);
            if (dto is null)
                return new(TicketProgressProbeStatus.InvalidResponse, null, "Empty response body");

            var snapshot = new TicketProgressSnapshot(
                dto.TicketId,
                dto.Status,
                dto.SagaEpoch,
                dto.ActiveSagaCorrelationId,
                dto.HasSuggestion,
                dto.HasAiDraft,
                dto.AiDraftCorrelationId,
                dto.AiDraftSagaEpoch,
                dto.AiDraftCategory,
                dto.AiDraftSuggestion,
                dto.AiDraftRelatedDocumentsJson);

            return new(TicketProgressProbeStatus.Found, snapshot, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ticket progress probe failed TicketId={TicketId}", ticketId);
            return new(TicketProgressProbeStatus.Unavailable, null, ex.Message);
        }
    }

    private sealed record TicketSagaProgressResponse(
        string TicketId,
        string Status,
        int SagaEpoch,
        Guid? ActiveSagaCorrelationId,
        bool HasSuggestion,
        bool HasAiDraft,
        Guid? AiDraftCorrelationId,
        int? AiDraftSagaEpoch,
        string? AiDraftCategory,
        string? AiDraftSuggestion,
        string? AiDraftRelatedDocumentsJson);
}
