using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Tests.TestSupport;
using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Options;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class ClientCredentialsTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsync_when_enabled_without_audience_returns_null_without_http()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("Token endpoint khong duoc goi khi thieu Audience."));
        var factory = new NamedHttpClientFactory();
        factory.Register(string.Empty, new HttpClient(handler, disposeHandler: false));

        var provider = new ClientCredentialsTokenProvider(
            Microsoft.Extensions.Options.Options.Create(new AzureAdOptions
            {
                Enabled = true,
                TenantId = "tenant-id",
                McpClientId = "mcp-client",
                McpClientSecret = "secret",
                Audience = ""
            }),
            factory);

        var token = await provider.GetTokenAsync();

        Assert.Null(token);
        Assert.Empty(handler.Requests);
    }
}
