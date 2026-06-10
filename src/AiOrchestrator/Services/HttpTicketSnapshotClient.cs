using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Messaging;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class HttpTicketSnapshotClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LocalMessagingOptions> localOptions) : ITicketSnapshotClient
{
    public const string HttpClientName = "ticket-service-snapshot";

    public async Task<TicketSnapshot?> GetTicketAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        var baseUrl = localOptions.Value.TicketServiceBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync($"{baseUrl}/tickets/{ticketId}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<TicketSnapshotDto>(cancellationToken: cancellationToken);
        if (dto is null)
            return null;

        return new TicketSnapshot(
            dto.Id,
            dto.Status,
            dto.Version,
            dto.HasAiSuggestion,
            !string.IsNullOrWhiteSpace(dto.FinalAnswer));
    }

    private sealed record TicketSnapshotDto(
        string Id,
        string Status,
        long Version,
        bool HasAiSuggestion,
        string? FinalAnswer);
}
