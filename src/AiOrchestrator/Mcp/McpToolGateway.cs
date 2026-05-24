using System.Text;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Mcp;

public sealed class McpToolGateway : IAsyncDisposable
{
    private readonly ServiceEndpointsOptions _endpoints;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private McpClient? _client;

    public McpToolGateway(IOptions<ServiceEndpointsOptions> endpoints)
    {
        _endpoints = endpoints.Value;
    }

    public async Task<string> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
        return ExtractText(result);
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null) return _client;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null) return _client;

            var baseUrl = _endpoints.McpToolServer.TrimEnd('/');
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"{baseUrl}/mcp")
            });
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
