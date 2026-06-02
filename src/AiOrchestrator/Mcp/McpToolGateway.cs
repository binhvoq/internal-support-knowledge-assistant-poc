using System.Text;
using System.Net.Http.Json;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.AiOrchestrator.Mcp;

public sealed class McpToolGateway : IAsyncDisposable
{
    public const string HttpClientName = "mcp-server";

    private readonly ServiceEndpointsOptions _endpoints;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TelemetryClient? _telemetry;
    private readonly ILogger<McpToolGateway> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private McpClient? _client;

    public McpToolGateway(
        IOptions<ServiceEndpointsOptions> endpoints,
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider services,
        ILogger<McpToolGateway> logger)
    {
        _endpoints = endpoints.Value;
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;
        _telemetry = services.GetService<TelemetryClient>();
        _logger = logger;
    }

    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        _logger.LogDebug("tools/list tra ve {Count} tool(s).", tools.Count);
        return tools.ToList();
    }

    public async Task<IReadOnlyList<McpToolPolicyDto>> ListToolPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _endpoints.McpToolServer.TrimEnd('/');
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        var policies = await httpClient.GetFromJsonAsync<IReadOnlyList<McpToolPolicyDto>>(
            $"{baseUrl}/internal/mcp/tool-policies",
            cancellationToken);
        return policies ?? [];
    }

    public Task<string> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
        => CallToolAsync(toolName, arguments, context: null, cancellationToken);

    public async Task<string> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        McpCallContext? context,
        CancellationToken cancellationToken = default)
    {
        var source = context?.Source ?? McpCallContext.SourceDirect;
        var ticketId = context?.TicketId ?? McpToolAudit.TryGetTicketId(arguments);
        var sagaId = context?.SagaCorrelationId;
        var outcome = "success";

        try
        {
            var client = await GetClientAsync(cancellationToken);
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
            return ExtractText(result);
        }
        catch (Exception ex)
        {
            outcome = "error";
            _logger.LogWarning(ex, "MCP tool {Tool} failed (source={Source}).", toolName, source);
            throw;
        }
        finally
        {
            McpToolAudit.TrackInvocation(
                _telemetry,
                _httpContextAccessor.HttpContext,
                source,
                toolName,
                outcome,
                sagaId,
                ticketId);
        }
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null) return _client;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null) return _client;

            var baseUrl = _endpoints.McpToolServer.TrimEnd('/');
            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"{baseUrl}/mcp")
                },
                httpClient,
                ownsHttpClient: false);
            _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            return _client;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string ExtractText(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
                sb.AppendLine(text.Text);
        }
        return sb.ToString().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _lock.Dispose();
    }
}
