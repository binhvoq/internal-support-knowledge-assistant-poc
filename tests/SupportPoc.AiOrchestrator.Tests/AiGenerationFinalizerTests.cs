using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class AiGenerationFinalizerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;
    private readonly Mock<IPublishEndpoint> _publish = new();

    public AiGenerationFinalizerTests()
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
    public async Task FinalizeSuccess_is_idempotent_when_already_completed()
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
                Status = AiGenerationAttemptStatus.Completed,
                Suggestion = "done",
                RelatedDocumentsJson = "[]",
                StartedAt = now,
                CompletedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        await using var finalizerDb = CreateDbContext();
        var finalizer = CreateFinalizer(finalizerDb);
        var outcome = await finalizer.FinalizeSuccessAsync(
            attemptId,
            "worker-a",
            new AiPipelineService.PipelineResult(SupportCategory.IT, "new", []),
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.SkippedTerminal, outcome);
        _publish.Verify(
            x => x.Publish(It.IsAny<ISuggestionGenerated>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FinalizeSuccess_skips_when_lease_owner_mismatch()
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
                LeaseOwner = "worker-b",
                LeaseUntil = now.AddMinutes(5),
                RelatedDocumentsJson = "[]",
                StartedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        await using var finalizerDb = CreateDbContext();
        var finalizer = CreateFinalizer(finalizerDb);
        var outcome = await finalizer.FinalizeSuccessAsync(
            attemptId,
            "worker-a",
            new AiPipelineService.PipelineResult(SupportCategory.IT, "new suggestion", []),
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.SkippedLeaseMismatch, outcome);
        var attempt = await finalizerDb.AiGenerationAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Running, attempt.Status);
        Assert.Null(attempt.Suggestion);
        Assert.Equal("worker-b", attempt.LeaseOwner);
        _publish.Verify(
            x => x.Publish(It.IsAny<ISuggestionGenerated>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FinalizeFailure_skips_when_lease_owner_mismatch()
    {
        var attemptId = Guid.NewGuid();
        var leaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);
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
                LeaseOwner = "worker-b",
                LeaseUntil = leaseUntil,
                RelatedDocumentsJson = "[]",
                RetryCount = 0,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var finalizerDb = CreateDbContext();
        var finalizer = CreateFinalizer(finalizerDb);
        var outcome = await finalizer.FinalizeFailureAsync(
            attemptId,
            "worker-a",
            "fail",
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.SkippedLeaseMismatch, outcome);
        var attempt = await finalizerDb.AiGenerationAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Running, attempt.Status);
        Assert.Equal("worker-b", attempt.LeaseOwner);
        Assert.Equal(leaseUntil, attempt.LeaseUntil);
        Assert.Equal(0, attempt.RetryCount);
        _publish.Verify(
            x => x.Publish(It.IsAny<ISuggestionGenerationFailed>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FinalizeFailure_schedules_retry_before_max_retries()
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
                LeaseOwner = "worker-a",
                LeaseUntil = now.AddMinutes(5),
                RelatedDocumentsJson = "[]",
                RetryCount = 0,
                StartedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        await using var finalizerDb = CreateDbContext();
        var finalizer = CreateFinalizer(finalizerDb, maxRetries: 3);
        var outcome = await finalizer.FinalizeFailureAsync(
            attemptId,
            "worker-a",
            "transient",
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.Applied, outcome);
        var attempt = await finalizerDb.AiGenerationAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Pending, attempt.Status);
        Assert.Equal(1, attempt.RetryCount);
        Assert.NotNull(attempt.NextRunAt);
        _publish.Verify(
            x => x.Publish(It.IsAny<ISuggestionGenerationFailed>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FinalizeFailure_publishes_when_retries_exhausted()
    {
        var attemptId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();
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
                LeaseOwner = "worker-a",
                LeaseUntil = now.AddMinutes(5),
                RelatedDocumentsJson = "[]",
                RetryCount = 3,
                StartedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        ISuggestionGenerationFailed? published = null;
        _publish.Setup(x => x.Publish(It.IsAny<ISuggestionGenerationFailed>(), It.IsAny<CancellationToken>()))
            .Callback<ISuggestionGenerationFailed, CancellationToken>((evt, _) => published = evt)
            .Returns(Task.CompletedTask);

        await using var finalizerDb = CreateDbContext();
        var finalizer = CreateFinalizer(finalizerDb, maxRetries: 3);
        var outcome = await finalizer.FinalizeFailureAsync(
            attemptId,
            "worker-a",
            "permanent",
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.Applied, outcome);
        var attempt = await finalizerDb.AiGenerationAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Failed, attempt.Status);
        Assert.NotNull(published);
        Assert.Equal(sagaId, published!.SagaId);
    }

    [Fact]
    public async Task FinalizeSuccess_skips_stale_write_when_row_changed_after_read()
    {
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedRunningAttemptAsync(attemptId, "worker-a", now);

        await using var staleDb = CreateDbContext();
        _ = await staleDb.AiGenerationAttempts
            .FirstAsync(x => x.AttemptId == attemptId, CancellationToken.None);

        await ReclaimLeaseAsync(attemptId, "worker-b", now.AddMinutes(10));

        var finalizer = CreateFinalizer(staleDb);
        var outcome = await finalizer.FinalizeSuccessAsync(
            attemptId,
            "worker-a",
            new AiPipelineService.PipelineResult(SupportCategory.IT, "new suggestion", []),
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.SkippedStaleWrite, outcome);

        await using var verifyDb = CreateDbContext();
        var attempt = await verifyDb.AiGenerationAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Running, attempt.Status);
        Assert.Equal("worker-b", attempt.LeaseOwner);
        Assert.Null(attempt.Suggestion);
    }

    [Fact]
    public async Task FinalizeFailure_skips_stale_write_when_row_changed_after_read()
    {
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddMinutes(5);
        await SeedRunningAttemptAsync(attemptId, "worker-a", now, leaseUntil);

        await using var staleDb = CreateDbContext();
        _ = await staleDb.AiGenerationAttempts
            .FirstAsync(x => x.AttemptId == attemptId, CancellationToken.None);

        var reclaimedLeaseUntil = now.AddMinutes(10);
        await ReclaimLeaseAsync(attemptId, "worker-b", reclaimedLeaseUntil);

        var finalizer = CreateFinalizer(staleDb, maxRetries: 3);
        var outcome = await finalizer.FinalizeFailureAsync(
            attemptId,
            "worker-a",
            "transient",
            CancellationToken.None);

        Assert.Equal(AiGenerationFinalizeOutcome.SkippedStaleWrite, outcome);

        await using var verifyDb = CreateDbContext();
        var attempt = await verifyDb.AiGenerationAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(AiGenerationAttemptStatus.Running, attempt.Status);
        Assert.Equal("worker-b", attempt.LeaseOwner);
        Assert.Equal(reclaimedLeaseUntil, attempt.LeaseUntil);
        Assert.Equal(0, attempt.RetryCount);
    }

    private async Task SeedRunningAttemptAsync(
        Guid attemptId,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset? leaseUntil = null)
    {
        await using var db = CreateDbContext();
        db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = attemptId,
            SagaId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            Question = "q",
            Status = AiGenerationAttemptStatus.Running,
            LeaseOwner = leaseOwner,
            LeaseUntil = leaseUntil ?? now.AddMinutes(5),
            RelatedDocumentsJson = "[]",
            RetryCount = 0,
            StartedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task ReclaimLeaseAsync(Guid attemptId, string leaseOwner, DateTimeOffset leaseUntil)
    {
        await using var db = CreateDbContext();
        var observed = await db.AiGenerationAttempts
            .AsNoTracking()
            .SingleAsync(x => x.AttemptId == attemptId);
        var nextRowVersion = IncrementRowVersion(observed.RowVersion);

        await db.AiGenerationAttempts
            .Where(x => x.AttemptId == attemptId && x.RowVersion == observed.RowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseOwner, leaseOwner)
                .SetProperty(x => x.LeaseUntil, leaseUntil)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.RowVersion, nextRowVersion));
    }

    private static byte[] IncrementRowVersion(byte[] current)
    {
        var next = (byte[])current.Clone();
        for (var i = next.Length - 1; i >= 0; i--)
        {
            if (++next[i] != 0)
                break;
        }

        return next;
    }

    private OrchestratorDbContext CreateDbContext() => new(_options);

    private AiGenerationFinalizer CreateFinalizer(OrchestratorDbContext db, int maxRetries = 3) =>
        new(
            db,
            _publish.Object,
            Microsoft.Extensions.Options.Options.Create(new AutoSuggestionOptions { MaxGenerationRetries = maxRetries }),
            NullLogger<AiGenerationFinalizer>.Instance);

    public void Dispose()
    {
        _connection.Dispose();
        _publish.Reset();
    }
}
