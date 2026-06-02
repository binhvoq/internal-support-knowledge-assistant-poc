using Microsoft.SemanticKernel;

namespace SupportPoc.AiOrchestrator.Services;

/// <summary>
/// Reserved AI runtime guard.
///
/// Tool-level authorization now comes from the MCP server policy contract and is applied
/// before Semantic Kernel advertises functions to the model. Resource-level authorization
/// stays in the owning API, for example TicketService.CanReadTicket.
///
/// Keep this filter intentionally pass-through until there is a concrete runtime AI risk
/// requirement such as human approval, DLP/secret checks, argument risk scoring, tool-call
/// budgets, or high-risk action audit immediately before a function invocation.
/// </summary>
public sealed class McpRoleInvocationFilter : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        await next(context);
    }
}
