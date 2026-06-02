using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.McpToolServer.Tools;
using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class McpSecurityTests
{
    [Fact]
    public void Agent_role_gets_privileged_tools_from_mcp_policy_contract()
    {
        var catalog = new McpToolPolicyCatalog(SupportToolPolicyCatalog.FromToolType<SupportTools>());

        var allowed = catalog.AllowedToolsForRoles([AppRoleNames.Agent]);

        Assert.Contains("create_ticket", allowed);
        Assert.Contains("get_ticket", allowed);
        Assert.Contains("update_ticket_status", allowed);
        Assert.Contains("search_knowledge", allowed);
    }

    [Fact]
    public void Employee_role_does_not_get_cross_ticket_lookup_tool()
    {
        var catalog = new McpToolPolicyCatalog(SupportToolPolicyCatalog.FromToolType<SupportTools>());

        var allowed = catalog.AllowedToolsForRoles([AppRoleNames.Employee]);

        Assert.Contains("search_knowledge", allowed);
        Assert.Contains("list_support_categories", allowed);
        Assert.DoesNotContain("get_ticket", allowed);
        Assert.DoesNotContain("create_ticket", allowed);
        Assert.DoesNotContain("update_ticket_status", allowed);
    }

    [Fact]
    public void Unknown_role_gets_no_tools()
    {
        var catalog = new McpToolPolicyCatalog(SupportToolPolicyCatalog.FromToolType<SupportTools>());

        var allowed = catalog.AllowedToolsForRoles(["Unknown.Role"]);

        Assert.Empty(allowed);
    }

    [Fact]
    public void Every_mcp_tool_policy_is_generated_from_explicit_tool_name_attributes()
    {
        var policies = SupportToolPolicyCatalog.FromToolType<SupportTools>();

        Assert.Equal(5, policies.Count);
        Assert.All(policies, policy => Assert.False(string.IsNullOrWhiteSpace(policy.ToolName)));
        Assert.Contains(policies, policy => policy.ToolName == "get_ticket" && policy.AllowedRoles.SequenceEqual([AppRoleNames.Agent]));
        Assert.Contains(policies, policy => policy.ToolName == "search_knowledge" && policy.AllowedRoles.Contains(AppRoleNames.Employee));
    }
}
