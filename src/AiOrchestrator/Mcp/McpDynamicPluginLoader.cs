using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

namespace SupportPoc.AiOrchestrator.Mcp;

public sealed class McpDynamicPluginLoader
{
    private readonly McpToolGateway _gateway;
    private readonly ILogger<McpDynamicPluginLoader> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private McpToolCatalog? _catalog;

    public McpDynamicPluginLoader(McpToolGateway gateway, ILogger<McpDynamicPluginLoader> logger)
    {
        _gateway = gateway;
        _logger = logger;
    }

    public bool IsEnabled => _gateway.IsEnabled;

    public async Task<McpToolCatalog> LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is not null)
            return _catalog;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is not null)
                return _catalog;

            if (!_gateway.IsEnabled)
            {
                _catalog = new McpToolCatalog([]);
                _logger.LogInformation("MCP tool server disabled by configuration; bo qua tools/list.");
                return _catalog;
            }

            var tools = await _gateway.ListToolsAsync(cancellationToken);
            _catalog = new McpToolCatalog(tools);
            _logger.LogInformation(
                "MCP tools/list: {Count} tool(s) — {Names}",
                tools.Count,
                string.Join(", ", tools.Select(t => t.Name)));
            return _catalog;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RegisterWithKernelAsync(Kernel kernel, CancellationToken cancellationToken = default)
    {
        var catalog = await LoadCatalogAsync(cancellationToken);
        if (catalog.Tools.Count == 0)
        {
            _logger.LogWarning("MCP tools/list tra ve rong — khong dang ky plugin.");
            return;
        }

        if (kernel.Plugins.TryGetPlugin("Mcp", out _))
            return;

        var functions = catalog.Tools
            .Select(tool => tool.AsKernelFunction())
            .ToList();

        kernel.Plugins.AddFromFunctions("Mcp", functions);
        _logger.LogInformation("Da dang ky {Count} MCP tool(s) vao Semantic Kernel.", functions.Count);
    }
}
