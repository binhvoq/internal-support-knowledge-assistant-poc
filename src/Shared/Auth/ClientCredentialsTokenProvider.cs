using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Options;

namespace SupportPoc.Shared.Auth;

/// <summary>OAuth2 client credentials for MCP / orchestrator → API (Support.Service).</summary>
public sealed class ClientCredentialsTokenProvider
{
    private readonly AzureAdOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public ClientCredentialsTokenProvider(IOptions<AzureAdOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return null;

        var clientId = _options.McpClientId ?? _options.ClientId;
        var secret = _options.McpClientSecret ?? _options.ClientSecret;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(_options.TenantId))
            return null;

        if (_cachedToken is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return _cachedToken;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return _cachedToken;

            var audience = string.IsNullOrWhiteSpace(_options.Audience)
                ? $"api://{clientId}"
                : _options.Audience;
            var scope = $"{audience}/.default";
            var tokenUrl = $"{_options.Instance.TrimEnd('/')}/{_options.TenantId}/oauth2/v2.0/token";

            using var http = _httpClientFactory.CreateClient();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = scope,
            });

            using var response = await http.PostAsync(tokenUrl, form, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Client credentials failed ({(int)response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            _cachedToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed class EntraBearerTokenHandler(ClientCredentialsTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
