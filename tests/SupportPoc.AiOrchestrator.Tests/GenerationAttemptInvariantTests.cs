using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportPoc.AiOrchestrator.Consumers;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class GenerationAttemptInvariantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public GenerationAttemptInvariantTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task TrySupersede_marks_running_attempt_superseded()
    {
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDbContext())
        {
            db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
            {
                AttemptId = attemptId,
                SagaId = Guid.NewGuid(),
                JobId = Guid.NewGuid(),
                TicketId = TestTicketIds.Default,
                Question = "q",
                Status = AiGenerationAttemptStatus.Running,
                LeaseOwner = "worker-1",
                LeaseUntil = now.AddMinutes(5),
                RelatedDocumentsJson = "[]",
                StartedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        await using var lifecycleDb = CreateDbContext();
        var lifecycle = new AiGenerationAttemptLifecycle(lifecycleDb, NullLogger<AiGenerationAttemptLifecycle>.Instance);
        var outcome = await lifecycle.TrySupersedeAsync(attemptId, "hard timeout", CancellationToken.None);

        Assert.Equal(SupersedeAttemptOutcome.Applied, outcome);
        var row = await lifecycleDb.AiGenerationAttempts.SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Superseded, row.Status);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseUntil);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task PrepareGenerationRetry_supersedes_active_attempt_before_new_attempt_id()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var oldAttemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDbContext())
        {
            db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
            {
                AttemptId = oldAttemptId,
                SagaId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                Question = "q",
                Status = AiGenerationAttemptStatus.Running,
                LeaseOwner = "worker-1",
                LeaseUntil = now.AddMinutes(-1),
                RelatedDocumentsJson = "[]",
                StartedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var saga = new TicketSuggestionSaga
        {
            CorrelationId = sagaId,
            JobId = jobId,
            TicketId = TestTicketIds.Default,
            Question = "q",
            OriginalCategory = SupportCategory.IT,
            CurrentAttemptId = oldAttemptId,
            CurrentAttemptIssuedAt = now,
            PendingReconcileAction = ReconcileActions.Retry
        };

        await using var activityDb = CreateDbContext();
        var activity = new PrepareGenerationRetryActivity(
            new AiGenerationAttemptReader(activityDb),
            new AiGenerationAttemptLifecycle(activityDb, NullLogger<AiGenerationAttemptLifecycle>.Instance),
            NullLogger<TicketSuggestionStateMachine>.Instance);

        var context = new Mock<BehaviorContext<TicketSuggestionSaga>>();
        context.SetupGet(x => x.Saga).Returns(saga);
        context.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);

        await activity.Execute(context.Object, Mock.Of<IBehavior<TicketSuggestionSaga>>());

        Assert.Equal(ReconcileActions.Retry, saga.PendingReconcileAction);
        Assert.NotEqual(oldAttemptId, saga.CurrentAttemptId);
        Assert.Equal(1, saga.RetryCount);

        var oldRow = await activityDb.AiGenerationAttempts.SingleAsync(x => x.AttemptId == oldAttemptId);
        Assert.Equal(AiGenerationAttemptStatus.Superseded, oldRow.Status);
    }

    [Fact]
    public async Task PrepareGenerationRetry_defers_when_supersede_has_concurrency_conflict()
    {
        var sagaId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDbContext())
        {
            db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
            {
                AttemptId = attemptId,
                SagaId = sagaId,
                JobId = Guid.NewGuid(),
                TicketId = TestTicketIds.Default,
                Question = "q",
                Status = AiGenerationAttemptStatus.Running,
                LeaseOwner = "worker-1",
                LeaseUntil = now.AddMinutes(5),
                RelatedDocumentsJson = "[]",
                StartedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var saga = new TicketSuggestionSaga
        {
            CorrelationId = sagaId,
            CurrentAttemptId = attemptId,
            CurrentAttemptIssuedAt = now,
            PendingReconcileAction = ReconcileActions.Retry
        };

        var lifecycle = new Mock<IAiGenerationAttemptLifecycle>();
        lifecycle.Setup(x => x.TrySupersedeAsync(attemptId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SupersedeAttemptOutcome.ConcurrencyConflict);

        var reader = new Mock<IAiGenerationAttemptReader>();
        reader.Setup(x => x.GetByAttemptIdAsync(attemptId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationAttemptSnapshot(
                attemptId,
                AiGenerationAttemptStatus.Running,
                now.AddMinutes(5),
                now,
                null,
                null,
                "[]",
                null));

        var activity = new PrepareGenerationRetryActivity(
            reader.Object,
            lifecycle.Object,
            NullLogger<TicketSuggestionStateMachine>.Instance);

        var context = new Mock<BehaviorContext<TicketSuggestionSaga>>();
        context.SetupGet(x => x.Saga).Returns(saga);
        context.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);

        await activity.Execute(context.Object, Mock.Of<IBehavior<TicketSuggestionSaga>>());

        Assert.Equal(ReconcileActions.WaitForGeneration, saga.PendingReconcileAction);
        Assert.Equal(attemptId, saga.CurrentAttemptId);
        Assert.Equal(0, saga.RetryCount);
    }

    [Fact]
    public async Task TryClaimNextAttempt_does_not_claim_superseded_attempt()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDbContext();
        db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = Guid.NewGuid(),
            SagaId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            Question = "q",
            Status = AiGenerationAttemptStatus.Superseded,
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            CompletedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var claimed = await AiGenerationWorkerService.TryClaimNextAttemptAsync(
            db,
            "worker-1",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Null(claimed);
    }

    [Fact]
    public async Task FinalizeSuccess_skips_superseded_attempt_without_publish()
    {
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDbContext())
        {
            db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
            {
                AttemptId = attemptId,
                SagaId = Guid.NewGuid(),
                JobId = Guid.NewGuid(),
                TicketId = TestTicketIds.Default,
                Question = "q",
                Status = AiGenerationAttemptStatus.Superseded,
                RelatedDocumentsJson = "[]",
                StartedAt = now,
                CompletedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        await using var finalizerDb = CreateDbContext();
        var finalizer = new AiGenerationFinalizer(
            finalizerDb,
            publish.Object,
            Microsoft.Extensions.Options.Options.Create(new AutoSuggestionOptions()),
            NullLogger<AiGenerationFinalizer>.Instance);

        var outcome = await finalizer.FinalizeSuccessAsync(
            attemptId,
            "worker-1",
            new AiPipelineService.PipelineResult(SupportCategory.IT, "late", []),
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.SkippedTerminal, outcome);
        publish.Verify(
            x => x.Publish(It.IsAny<ISuggestionGenerated>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Enqueue_blocks_second_active_attempt_for_same_job()
    {
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = CreateDbContext())
        {
            db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
            {
                AttemptId = Guid.NewGuid(),
                SagaId = Guid.NewGuid(),
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                Question = "q",
                Status = AiGenerationAttemptStatus.Running,
                LeaseOwner = "worker-1",
                LeaseUntil = now.AddMinutes(5),
                RelatedDocumentsJson = "[]",
                StartedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var msg = new GenerateSuggestionRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            jobId,
            TestTicketIds.Default,
            "q",
            SupportCategory.IT);

        await using var consumerDb = CreateDbContext();
        var consumer = new GenerateSuggestionRequestedConsumer(
            consumerDb,
            new AiGenerationAttemptLifecycle(consumerDb, NullLogger<AiGenerationAttemptLifecycle>.Instance),
            NullLogger<GenerateSuggestionRequestedConsumer>.Instance);

        var published = new List<ISuggestionGenerated>();
        var context = new Mock<ConsumeContext<IGenerateSuggestionRequested>>();
        context.SetupGet(x => x.Message).Returns(msg);
        context.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);
        context.Setup(x => x.Publish(It.IsAny<ISuggestionGenerated>(), It.IsAny<CancellationToken>()))
            .Callback<ISuggestionGenerated, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);

        await consumer.Consume(context.Object);

        Assert.Empty(published);
        Assert.Equal(1, await consumerDb.AiGenerationAttempts.CountAsync());
    }

    public void Dispose() => _connection.Dispose();

    private OrchestratorDbContext CreateDbContext() => new(_options);
}
