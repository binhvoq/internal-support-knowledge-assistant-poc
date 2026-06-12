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
    private readonly Mock<IAiPipelineService> _pipeline = new();

    public GenerateSuggestionRequestedConsumerIdempotencyTests()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new OrchestratorDbContext(options);
    }

    [Fact]
    public async Task Duplicate_AttemptId_replays_completed_outcome_without_running_pipeline()
    {
        var msg = CreateMessage();
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = msg.AttemptId,
            SagaId = msg.SagaId,
            JobId = msg.JobId,
            TicketId = msg.TicketId,
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
        var context = CreateContext(msg, published);

        var consumer = new GenerateSuggestionRequestedConsumer(
            _pipeline.Object,
            _db,
            NullLogger<GenerateSuggestionRequestedConsumer>.Instance);

        await consumer.Consume(context);

        _pipeline.Verify(
            p => p.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var generated = Assert.Single(published);
        Assert.Equal("cached suggestion", generated.Suggestion);
        Assert.Equal(1, await _db.AiGenerationAttempts.CountAsync());
    }

    [Fact]
    public async Task Duplicate_AttemptId_while_running_defers_without_second_claim()
    {
        var msg = CreateMessage();
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = msg.AttemptId,
            SagaId = msg.SagaId,
            JobId = msg.JobId,
            TicketId = msg.TicketId,
            Status = AiGenerationAttemptStatus.Running,
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var consumer = new GenerateSuggestionRequestedConsumer(
            _pipeline.Object,
            _db,
            NullLogger<GenerateSuggestionRequestedConsumer>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.Consume(CreateContext(msg, published: [])));

        _pipeline.Verify(
            p => p.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(1, await _db.AiGenerationAttempts.CountAsync());
    }

    private static GenerateSuggestionRequested CreateMessage() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "TCK-AI-1",
            "How do I reset my password?",
            SupportCategory.IT);

    private static ConsumeContext<IGenerateSuggestionRequested> CreateContext(
        IGenerateSuggestionRequested message,
        List<ISuggestionGenerated> published)
    {
        var mock = new Mock<ConsumeContext<IGenerateSuggestionRequested>>();
        mock.Setup(x => x.Message).Returns(message);
        mock.Setup(x => x.CancellationToken).Returns(CancellationToken.None);
        mock.Setup(x => x.Publish(It.IsAny<ISuggestionGenerated>(), It.IsAny<CancellationToken>()))
            .Callback<ISuggestionGenerated, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.Publish(It.IsAny<ISuggestionGenerationFailed>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock.Object;
    }

    public void Dispose() => _db.Dispose();
}
