using Microsoft.SemanticKernel;

namespace SupportPoc.AiOrchestrator.Mcp;

public sealed class McpToolAccessService(
    McpDynamicPluginLoader loader,
    McpToolGateway gateway,
    ILogger<McpToolAccessService> logger)
{
    public async Task<IReadOnlyList<KernelFunction>> GetAllowedFunctionsAsync(
        Kernel kernel,
        IEnumerable<string>? roles,
        CancellationToken cancellationToken = default)
    {
        var catalog = await loader.LoadCatalogAsync(cancellationToken);
        await loader.RegisterWithKernelAsync(kernel, cancellationToken);

        if (!kernel.Plugins.TryGetPlugin("Mcp", out var plugin))
            return [];

        if (roles is null)
            return plugin.ToList();

        var policies = new McpToolPolicyCatalog(await gateway.ListToolPoliciesAsync(cancellationToken));
        policies.ValidateAgainst(catalog);

        var allowedNames = policies.AllowedToolsForRoles(roles);
        if (allowedNames.Count == 0)
        {
            logger.LogWarning("No MCP tools are allowed for current roles.");
            return [];
        }

        var functions = plugin
            .Where(function => allowedNames.Contains(function.Name))
            .ToList();

        logger.LogInformation(
            "Allowed MCP tools advertised to Semantic Kernel: {Tools}",
            string.Join(", ", functions.Select(function => function.Name)));
        return functions;
    }

    public async Task<IReadOnlySet<string>> GetAllowedToolNamesAsync(
        IEnumerable<string>? roles,
        CancellationToken cancellationToken = default)
    {
        var catalog = await loader.LoadCatalogAsync(cancellationToken);
        if (roles is null)
            return catalog.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var policies = new McpToolPolicyCatalog(await gateway.ListToolPoliciesAsync(cancellationToken));
        policies.ValidateAgainst(catalog);
        return policies.AllowedToolsForRoles(roles);
    }
}
