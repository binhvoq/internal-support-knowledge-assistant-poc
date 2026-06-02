using System.Reflection;
using ModelContextProtocol.Server;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.McpToolServer.Tools;

public static class SupportToolPolicyCatalog
{
    public static IReadOnlyList<McpToolPolicyDto> FromToolType<TTool>() => FromToolType(typeof(TTool));

    public static IReadOnlyList<McpToolPolicyDto> FromToolType(Type toolType)
    {
        var policies = new List<McpToolPolicyDto>();
        var methods = toolType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

        foreach (var method in methods)
        {
            var tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
            var policy = method.GetCustomAttribute<SupportToolPolicyAttribute>();
            if (policy is null)
                throw new InvalidOperationException($"MCP tool method '{toolType.Name}.{method.Name}' is missing SupportToolPolicyAttribute.");

            var toolName = tool.Name;
            if (string.IsNullOrWhiteSpace(toolName))
                throw new InvalidOperationException($"MCP tool method '{toolType.Name}.{method.Name}' must declare an explicit McpServerTool Name.");

            policies.Add(new McpToolPolicyDto(
                toolName,
                policy.Roles.ToArray(),
                policy.Risk,
                policy.Notes));
        }

        return policies
            .OrderBy(policy => policy.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
