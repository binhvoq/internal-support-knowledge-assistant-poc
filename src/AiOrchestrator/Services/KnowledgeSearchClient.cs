using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

public interface IKnowledgeSearchClient
{
    Task<IReadOnlyList<RelatedDocument>> SearchAsync(
        string query,
        string? category,
        CancellationToken cancellationToken = default);
}

public sealed class KnowledgeSearchClient : IKnowledgeSearchClient
{
    public const string HttpClientName = "knowledge-service";

    private readonly ServiceEndpointsOptions _endpoints;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<KnowledgeSearchClient> _logger;

    public KnowledgeSearchClient(
        IOptions<ServiceEndpointsOptions> endpoints,
        IHttpClientFactory httpClientFactory,
        ILogger<KnowledgeSearchClient> logger)
    {
        _endpoints = endpoints.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RelatedDocument>> SearchAsync(
        string query,
        string? category,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_endpoints.KnowledgeService.TrimEnd('/')}/search?query={Uri.EscapeDataString(query)}&mode=hybrid&rerank=none";
        if (!string.IsNullOrWhiteSpace(category) && category != SupportCategory.Other)
            url += $"&category={Uri.EscapeDataString(category)}";

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var snippet = body.Length > 300 ? body[..300] + "..." : body;
            _logger.LogWarning(
                "KnowledgeService search HTTP {(StatusCode)} cho query {QueryLength} chars — {Snippet}",
                (int)response.StatusCode,
                query.Length,
                snippet);
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content.ReadFromJsonAsync<KnowledgeSearchResponse>(cancellationToken);
        var results = payload?.Results ?? [];
        if (results.Count == 0)
        {
            _logger.LogInformation(
                "KnowledgeService tra ve 0 ket qua cho query {QueryPreview}",
                TruncateQuery(query));
        }

        return results;
    }

    private static string TruncateQuery(string query) =>
        query.Length <= 80 ? query : query[..80] + "...";

    private sealed record KnowledgeSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<RelatedDocument> Results);
}
