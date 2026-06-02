using ModelContextProtocol.Client;

namespace SupportPoc.AiOrchestrator.Mcp;

public sealed class McpToolCatalog
{
    private readonly Dictionary<string, McpClientTool> _byName;

    public McpToolCatalog(IReadOnlyList<McpClientTool> tools)
    {
        Tools = tools;
        _byName = tools
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<McpClientTool> Tools { get; }

    public bool Has(string toolName) => _byName.ContainsKey(toolName);

    public string Require(string toolName)
    {
        if (!_byName.ContainsKey(toolName))
            throw new InvalidOperationException($"MCP tool '{toolName}' khong co trong tools/list.");
        return toolName;
    }

    public string DescribeForPrompt(IEnumerable<string>? allowedToolNames = null)
    {
        var tools = Tools;
        if (allowedToolNames is not null)
        {
            var allowed = allowedToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            tools = Tools.Where(t => allowed.Contains(t.Name)).ToList();
        }

        if (tools.Count == 0)
            return "Khong co MCP tool nao.";

        return string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description ?? "(khong co mo ta)"}"));
    }
}
