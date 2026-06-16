using System.Net;

namespace SupportPoc.AiOrchestrator.Options;

public sealed class ServiceEndpointsOptions
{
    public const string SectionName = "Services";
    public string TicketService { get; set; } = "http://localhost:5001";
    public string KnowledgeService { get; set; } = "http://localhost:5002";
    public string McpToolServer { get; set; } = "http://localhost:5004";

    public bool IsMcpToolServerEnabled => !IsMcpToolServerDisabled;

    public bool IsMcpToolServerDisabled =>
        string.IsNullOrWhiteSpace(McpToolServer)
        || IsLoopbackEndpoint(McpToolServer);

    public string? McpToolServerHost => IsMcpToolServerEnabled ? ResolveHost(McpToolServer) : null;

    public string? McpToolServerValue => IsMcpToolServerEnabled ? McpToolServer.TrimEnd('/') : null;

    private static bool IsLoopbackEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || value.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || value.Contains("::1", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(uri.Host, out var address))
            return IPAddress.IsLoopback(address);

        return false;
    }

    private static string? ResolveHost(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return value;

        return uri.Host;
    }
}
