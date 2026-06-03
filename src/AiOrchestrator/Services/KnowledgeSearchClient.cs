using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class KnowledgeSearchClient
{
    public const string HttpClientName = "knowledge-service";

    private readonly ServiceEndpointsOptions _endpoints;
    private readonly IHttpClientFactory _httpClientFactory;

    public KnowledgeSearchClient(
        IOptions<ServiceEndpointsOptions> endpoints,
        IHttpClientFactory httpClientFactory)
    {
        _endpoints = endpoints.Value;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<RelatedDocument>> SearchAsync(
        string query,
        string? category,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_endpoints.KnowledgeService.TrimEnd('/')}/search?query={Uri.EscapeDataString(query)}&mode=hybrid";
        if (!string.IsNullOrWhiteSpace(category) && category != SupportCategory.Other)
            url += $"&category={Uri.EscapeDataString(category)}";

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var response = await client.GetFromJsonAsync<KnowledgeSearchResponse>(url, cancellationToken);
        return response?.Results ?? [];
    }

    private sealed record KnowledgeSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<RelatedDocument> Results);
}
