using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.AiOrchestrator.Tests.TestSupport;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class AiPipelineServiceTests
{
    [Fact]
    public async Task RunAsync_uses_category_for_result_but_searches_all_knowledge()
    {
        var knowledge = new CapturingKnowledgeSearchClient();
        var provider = new ServiceCollection()
            .AddSingleton<IOptions<AzureOpenAIOptions>>(
                Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false }))
            .BuildServiceProvider();
        var pipeline = new AiPipelineService(
            knowledge,
            new IChatCompletionServiceAccessor(
                provider,
                Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false })),
            Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false }),
            NullLogger<AiPipelineService>.Instance);

        var result = await pipeline.RunAsync(
            "Quy trinh xin nghi phep 3 ngay?",
            SupportCategory.IT,
            CancellationToken.None);

        Assert.Equal(SupportCategory.IT, result.Category);
        Assert.Null(knowledge.CapturedCategory);
        Assert.Equal("Quy trinh xin nghi phep 3 ngay?", knowledge.CapturedQuery);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_logs_and_returns_empty_when_search_fails()
    {
        var knowledge = new FailingKnowledgeSearchClient();
        var provider = new ServiceCollection()
            .AddSingleton<IOptions<AzureOpenAIOptions>>(
                Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false }))
            .BuildServiceProvider();
        var logger = new ListLogger<AiPipelineService>();
        var pipeline = new AiPipelineService(
            knowledge,
            new IChatCompletionServiceAccessor(
                provider,
                Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false })),
            Microsoft.Extensions.Options.Options.Create(new AzureOpenAIOptions { ChatEnabled = false }),
            logger);

        var results = await pipeline.SearchKnowledgeAsync("VPN khong ket noi", null, CancellationToken.None);

        Assert.Empty(results);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("KnowledgeService search that bai", StringComparison.Ordinal));
    }

    private sealed class FailingKnowledgeSearchClient : IKnowledgeSearchClient
    {
        public Task<IReadOnlyList<RelatedDocument>> SearchAsync(
            string query,
            string? category,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("Simulated KnowledgeService outage");
    }

    private sealed class CapturingKnowledgeSearchClient : IKnowledgeSearchClient
    {
        public string? CapturedQuery { get; private set; }
        public string? CapturedCategory { get; private set; } = "not-called";

        public Task<IReadOnlyList<RelatedDocument>> SearchAsync(
            string query,
            string? category,
            CancellationToken cancellationToken = default)
        {
            CapturedQuery = query;
            CapturedCategory = category;
            return Task.FromResult<IReadOnlyList<RelatedDocument>>(
            [
                new RelatedDocument
                {
                    DocumentId = "hr-policy",
                    Title = "Leave policy",
                    Content = "Nhan vien co the xin nghi phep theo quy trinh HR.",
                    Score = 1
                }
            ]);
        }
    }
}
