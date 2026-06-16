using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class ServiceEndpointsOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:5004")]
    [InlineData("http://127.0.0.1:5004")]
    [InlineData("http://[::1]:5004")]
    public void Mcp_tool_server_is_disabled_for_loopback_values(string value)
    {
        var options = new ServiceEndpointsOptions { McpToolServer = value };

        Assert.True(options.IsMcpToolServerDisabled);
        Assert.False(options.IsMcpToolServerEnabled);
        Assert.Null(options.McpToolServerHost);
        Assert.Null(options.McpToolServerValue);
    }

    [Fact]
    public void Mcp_tool_server_is_enabled_for_internal_url()
    {
        var options = new ServiceEndpointsOptions { McpToolServer = "https://mcp-tool-server.internal.example" };

        Assert.False(options.IsMcpToolServerDisabled);
        Assert.True(options.IsMcpToolServerEnabled);
        Assert.Equal("mcp-tool-server.internal.example", options.McpToolServerHost);
        Assert.Equal("https://mcp-tool-server.internal.example", options.McpToolServerValue);
    }
}
