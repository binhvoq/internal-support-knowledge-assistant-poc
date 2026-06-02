using System.Security.Claims;
using Microsoft.ApplicationInsights;

namespace SupportPoc.AiOrchestrator.Services;

/// <summary>Structured audit fields cho MCP tool invocation — App Insights custom event + SIEM.</summary>
public static class McpToolAudit
{
    public const string EventName = "McpToolInvocation";

    public static (string Oid, string Roles, string HttpCorrelationId) FromHttpContext(HttpContext? http)
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
        string source,
        string tool,
        string outcome,
        Guid? sagaCorrelationId = null,
        string? ticketId = null)
    {
        var (oid, roles, httpCorrelationId) = FromHttpContext(http);
        telemetry?.TrackEvent(EventName, new Dictionary<string, string>
        {
            ["source"] = source,
            ["tool"] = tool,
            ["outcome"] = outcome,
            ["oid"] = oid,
            ["roles"] = roles,
            ["httpCorrelationId"] = httpCorrelationId,
            ["sagaCorrelationId"] = sagaCorrelationId?.ToString() ?? "",
            ["ticketId"] = ticketId ?? "",
        });
    }

    public static string? TryGetTicketId(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        foreach (var key in new[] { "ticketId", "ticket_id", "id" })
        {
            if (arguments.TryGetValue(key, out var value) && value is not null)
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }

        return null;
    }
}
