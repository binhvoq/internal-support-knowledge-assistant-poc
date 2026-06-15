using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.AiOrchestrator.Tests.TestSupport;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class KnowledgeSearchClientTests
{
    private const string KnowledgeBaseUrl = "http://knowledge.test";

    [Fact]
    public async Task SearchAsync_when_http_error_logs_warning_and_throws()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("KnowledgeService unavailable", Encoding.UTF8, "text/plain")
            });
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SearchAsync("VPN loi", null, CancellationToken.None));

        Assert.NotNull(ex);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning &&
                 e.Message.Contains("KnowledgeService search HTTP 503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_when_empty_results_logs_information_and_returns_empty_list()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json")
            });
        var client = CreateClient(handler, out var logger);

        var results = await client.SearchAsync("VPN loi", null, CancellationToken.None);

        Assert.Empty(results);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information &&
                 e.Message.Contains("0 ket qua", StringComparison.Ordinal) &&
                 e.Message.Contains("VPN loi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_appends_category_when_not_other()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            Assert.Contains("category=HR", path, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json")
            };
        });
        var client = CreateClient(handler, out _);

        await client.SearchAsync("nghi phep", SupportCategory.HR, CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    private static KnowledgeSearchClient CreateClient(
        StubHttpMessageHandler handler,
        out ListLogger<KnowledgeSearchClient> logger)
    {
        var factory = new NamedHttpClientFactory();
        factory.Register(
            KnowledgeSearchClient.HttpClientName,
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(KnowledgeBaseUrl) });
        logger = new ListLogger<KnowledgeSearchClient>();
        return new KnowledgeSearchClient(
            Microsoft.Extensions.Options.Options.Create(new ServiceEndpointsOptions { KnowledgeService = KnowledgeBaseUrl }),
            factory,
            logger);
    }
}
