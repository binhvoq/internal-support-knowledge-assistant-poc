using SupportPoc.Shared.Contracts;

namespace SupportPoc.AiOrchestrator.Mcp;

public sealed class McpToolPolicyCatalog
{
    private readonly IReadOnlyList<McpToolPolicyDto> _policies;

    public McpToolPolicyCatalog(IReadOnlyList<McpToolPolicyDto> policies)
    {
        _policies = policies;
    }

    public IReadOnlySet<string> AllowedToolsForRoles(IEnumerable<string> roles)
    {
        var roleSet = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _policies
            .Where(policy => policy.AllowedRoles.Any(roleSet.Contains))
            .Select(policy => policy.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void ValidateAgainst(McpToolCatalog catalog)
    {
        var toolNames = catalog.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var policyNames = _policies
            .Select(policy => policy.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingInMcp = policyNames.Except(toolNames, StringComparer.OrdinalIgnoreCase).ToList();
        if (missingInMcp.Count > 0)
        {
            throw new InvalidOperationException(
                $"MCP tool policy references missing tool(s): {string.Join(", ", missingInMcp)}.");
        }

        var unclassified = toolNames.Except(policyNames, StringComparer.OrdinalIgnoreCase).ToList();
        if (unclassified.Count > 0)
        {
            throw new InvalidOperationException(
                $"MCP tools/list returned unclassified tool(s): {string.Join(", ", unclassified)}.");
        }
    }
}
