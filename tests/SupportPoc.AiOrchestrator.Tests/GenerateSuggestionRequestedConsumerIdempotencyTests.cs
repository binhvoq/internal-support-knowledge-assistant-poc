using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportPoc.AiOrchestrator.Consumers;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class GenerateSuggestionRequestedConsumerIdempotencyTests : IDisposable
{
    private readonly OrchestratorDbContext _db;

    public GenerateSuggestionRequestedConsumerIdempotencyTests()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new OrchestratorDbContext(options);
    }

    [Fact]
    public async Task Duplicate_AttemptId_replays_completed_outcome()
    {
        var msg = CreateMessage();
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = msg.AttemptId,
            SagaId = msg.SagaId,
            JobId = msg.JobId,
            TicketId = msg.TicketId,
            Question = msg.Question,
            RequestedCategory = msg.Category,
            Status = AiGenerationAttemptStatus.Completed,
            Category = SupportCategory.IT,
            Suggestion = "cached suggestion",
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            CompletedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var published = new List<ISuggestionGenerated>();
        var consumer = CreateConsumer();

        await consumer.Consume(CreateContext(msg, published));

        var generated = Assert.Single(published);
        Assert.Equal("cached suggestion", generated.Suggestion);
        Assert.Equal(1, await _db.AiGenerationAttempts.CountAsync());
    }

    [Theory]
    [InlineData(AiGenerationAttemptStatus.Pending)]
    [InlineData(AiGenerationAttemptStatus.Running)]
    public async Task Duplicate_AttemptId_while_pending_or_running_returns_without_throw(string status)
    {
        var msg = CreateMessage();
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = msg.AttemptId,
            SagaId = msg.SagaId,
            JobId = msg.JobId,
            TicketId = msg.TicketId,
            Question = msg.Question,
            RequestedCategory = msg.Category,
            Status = status,
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var consumer = CreateConsumer();
        var publishedGenerated = new List<ISuggestionGenerated>();
        var publishedFailed = new List<ISuggestionGenerationFailed>();
        var context = CreateContext(msg, publishedGenerated, publishedFailed);

        await consumer.Consume(context);

        Assert.Empty(publishedGenerated);
        Assert.Empty(publishedFailed);
        Assert.Equal(1, await _db.AiGenerationAttempts.CountAsync());
    }

    [Fact]
    public async Task New_request_enqueues_pending_attempt_without_publishing()
    {
        var msg = CreateMessage();
        var consumer = CreateConsumer();
        var publishedGenerated = new List<ISuggestionGenerated>();
        var publishedFailed = new List<ISuggestionGenerationFailed>();

        await consumer.Consume(CreateContext(msg, publishedGenerated, publishedFailed));

        Assert.Empty(publishedGenerated);
        Assert.Empty(publishedFailed);
        var attempt = await _db.AiGenerationAttempts.SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Pending, attempt.Status);
        Assert.Equal(msg.Question, attempt.Question);
        Assert.Equal(msg.Category, attempt.RequestedCategory);
    }

    private GenerateSuggestionRequestedConsumer CreateConsumer() =>
        new(
            _db,
            new AiGenerationAttemptLifecycle(_db, NullLogger<AiGenerationAttemptLifecycle>.Instance),
            NullLogger<GenerateSuggestionRequestedConsumer>.Instance);

    private static GenerateSuggestionRequested CreateMessage() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            TestTicketIds.AiConsumer,
            "How do I reset my password?",
            SupportCategory.IT);

    private static ConsumeContext<IGenerateSuggestionRequested> CreateContext(
        IGenerateSuggestionRequested message,
        List<ISuggestionGenerated> publishedGenerated,
        List<ISuggestionGenerationFailed>? publishedFailed = null)
    {
        publishedFailed ??= [];
        var mock = new Mock<ConsumeContext<IGenerateSuggestionRequested>>();
        mock.Setup(x => x.Message).Returns(message);
        mock.Setup(x => x.CancellationToken).Returns(CancellationToken.None);
        mock.Setup(x => x.Publish(It.IsAny<ISuggestionGenerated>(), It.IsAny<CancellationToken>()))
            .Callback<ISuggestionGenerated, CancellationToken>((evt, _) => publishedGenerated.Add(evt))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.Publish(It.IsAny<ISuggestionGenerationFailed>(), It.IsAny<CancellationToken>()))
            .Callback<ISuggestionGenerationFailed, CancellationToken>((evt, _) => publishedFailed.Add(evt))
            .Returns(Task.CompletedTask);
        return mock.Object;
    }

    public void Dispose() => _db.Dispose();
}
