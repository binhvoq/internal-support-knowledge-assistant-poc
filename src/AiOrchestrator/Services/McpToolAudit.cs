using System.Security.Claims;
using Microsoft.ApplicationInsights;

namespace SupportPoc.AiOrchestrator.Services;

/// <summary>Structured audit fields cho MCP tool invocation — App Insights custom event + SIEM.</summary>
public static class McpToolAudit
{
    public const string EventName = "McpToolInvocation";

    public static (string Oid, string Roles, string CorrelationId) FromHttpContext(HttpContext? http)
    {
        if (http is null)
            return ("", "", "");

        var oid = http.User.FindFirstValue("oid")
            ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "";
        var roles = string.Join(",", http.User.FindAll(ClaimTypes.Role).Select(c => c.Value));
        return (oid, roles, http.TraceIdentifier);
    }

    public static void TrackInvocation(
        TelemetryClient? telemetry,
        HttpContext? http,
        string tool,
        string outcome)
    {
        var (oid, roles, correlationId) = FromHttpContext(http);
        telemetry?.TrackEvent(EventName, new Dictionary<string, string>
        {
            ["outcome"] = outcome,
            ["tool"] = tool,
            ["oid"] = oid,
            ["roles"] = roles,
            ["correlationId"] = correlationId,
        });
    }
}
