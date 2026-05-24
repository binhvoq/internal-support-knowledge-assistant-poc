using System.Net.Http.Json;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Clients;

public sealed class TicketApiClient(HttpClient http)
{
    public Task<TicketDto?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        http.GetFromJsonAsync<TicketDto>($"/tickets/{id}", cancellationToken);

    public async Task<TicketDto?> PatchAsync(string id, object body, CancellationToken cancellationToken = default)
    {
        var response = await http.PatchAsJsonAsync($"/tickets/{id}", body, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TicketDto>(cancellationToken)
            : null;
    }
}
