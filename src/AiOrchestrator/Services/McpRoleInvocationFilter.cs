using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.SemanticKernel;
using SupportPoc.AiOrchestrator.Mcp;

namespace SupportPoc.AiOrchestrator.Services;

/// <summary>
/// Audit MCP plugin invocations from Semantic Kernel chat (không đi qua McpToolGateway).
/// Tool policy vẫn được áp trước khi advertise functions cho model.
/// </summary>
public sealed class McpRoleInvocationFilter : IFunctionInvocationFilter
{
    private const string McpPluginName = "Mcp";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TelemetryClient? _telemetry;

    public McpRoleInvocationFilter(IHttpContextAccessor httpContextAccessor, TelemetryClient? telemetry)
    {
        _httpContextAccessor = httpContextAccessor;
        _telemetry = telemetry;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        if (!string.Equals(context.Function.PluginName, McpPluginName, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var tool = context.Function.Name;
        var ticketId = McpToolAudit.TryGetTicketId(ToArgumentDictionary(context.Arguments));
        var outcome = "success";

        try
        {
            await next(context);
        }
        catch
        {
            outcome = "error";
            throw;
        }
        finally
        {
            McpToolAudit.TrackInvocation(
                _telemetry,
                _httpContextAccessor.HttpContext,
                McpCallContext.SourceChat,
                tool,
                outcome,
                ticketId: ticketId);
        }
    }

    private static IReadOnlyDictionary<string, object?>? ToArgumentDictionary(KernelArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        return arguments.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
