using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Options;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class EntraOutboundReadinessPolicyTests
{
    [Fact]
    public void EvaluateConfig_when_entra_disabled_is_ready()
    {
        var result = EntraOutboundReadinessPolicy.EvaluateConfig(new AzureAdOptions { Enabled = false });

        Assert.True(result.Ready);
        Assert.Contains("disabled", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateConfig_when_enabled_without_audience_is_not_ready()
    {
        var result = EntraOutboundReadinessPolicy.EvaluateConfig(new AzureAdOptions
        {
            Enabled = true,
            TenantId = "tenant",
            McpClientId = "mcp-client",
            McpClientSecret = "secret",
            ClientId = "api-client-id",
            Audience = ""
        });

        Assert.False(result.Ready);
        Assert.Contains("Audience", result.Detail);
    }

    [Fact]
    public void EvaluateConfig_when_enabled_with_audience_is_ready()
    {
        var result = EntraOutboundReadinessPolicy.EvaluateConfig(new AzureAdOptions
        {
            Enabled = true,
            TenantId = "tenant",
            McpClientId = "mcp-client",
            McpClientSecret = "secret",
            Audience = "api://support-api"
        });

        Assert.True(result.Ready);
    }
}
