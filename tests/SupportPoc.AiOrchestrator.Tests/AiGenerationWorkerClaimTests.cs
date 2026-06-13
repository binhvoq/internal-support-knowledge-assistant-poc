using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Services;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class AiGenerationWorkerClaimTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrchestratorDbContext _db;

    public AiGenerationWorkerClaimTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new OrchestratorDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task TryClaimNextAttempt_claims_pending_job()
    {
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = Guid.NewGuid(),
            SagaId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            Question = "reset password",
            Status = AiGenerationAttemptStatus.Pending,
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var claimed = await AiGenerationWorkerService.TryClaimNextAttemptAsync(
            _db,
            "worker-1",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(AiGenerationAttemptStatus.Running, claimed!.Status);
        Assert.Equal("worker-1", claimed.LeaseOwner);
        Assert.NotNull(claimed.LeaseUntil);
    }

    [Fact]
    public async Task TryClaimNextAttempt_reclaims_stale_running_lease()
    {
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = Guid.NewGuid(),
            SagaId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            Question = "reset password",
            Status = AiGenerationAttemptStatus.Running,
            LeaseOwner = "old-worker",
            LeaseUntil = now.AddMinutes(-1),
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var claimed = await AiGenerationWorkerService.TryClaimNextAttemptAsync(
            _db,
            "worker-2",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal("worker-2", claimed!.LeaseOwner);
    }

    [Fact]
    public async Task TryClaimNextAttempt_skips_future_next_run_at()
    {
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = Guid.NewGuid(),
            SagaId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            Question = "reset password",
            Status = AiGenerationAttemptStatus.Pending,
            NextRunAt = now.AddMinutes(10),
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var claimed = await AiGenerationWorkerService.TryClaimNextAttemptAsync(
            _db,
            "worker-1",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Null(claimed);
    }

    [Fact]
    public async Task TryClaimNextAttempt_uses_conditional_update()
    {
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = attemptId,
            SagaId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            Question = "reset password",
            Status = AiGenerationAttemptStatus.Pending,
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var first = await AiGenerationWorkerService.TryClaimNextAttemptAsync(
            _db,
            "worker-1",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        var second = await AiGenerationWorkerService.TryClaimNextAttemptAsync(
            _db,
            "worker-2",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal("worker-1", first!.LeaseOwner);

        var stored = await _db.AiGenerationAttempts.AsNoTracking().SingleAsync(x => x.AttemptId == attemptId);
        Assert.Equal("worker-1", stored.LeaseOwner);
        Assert.Equal(AiGenerationAttemptStatus.Running, stored.Status);
    }

    [Fact]
    public async Task TryRenewLease_only_renews_current_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        var originalLeaseUntil = now.AddMinutes(1);
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = attemptId,
            SagaId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            Question = "reset password",
            Status = AiGenerationAttemptStatus.Running,
            LeaseOwner = "worker-a",
            LeaseUntil = originalLeaseUntil,
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var renewed = await AiGenerationWorkerService.TryRenewLeaseAsync(
            _db,
            attemptId,
            "worker-a",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        var wrongOwner = await AiGenerationWorkerService.TryRenewLeaseAsync(
            _db,
            attemptId,
            "worker-b",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.True(renewed);
        Assert.False(wrongOwner);

        var stored = await _db.AiGenerationAttempts.AsNoTracking().SingleAsync(x => x.AttemptId == attemptId);
        Assert.Equal("worker-a", stored.LeaseOwner);
        Assert.True(stored.LeaseUntil > originalLeaseUntil);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
