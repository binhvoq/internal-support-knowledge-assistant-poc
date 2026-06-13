using SupportPoc.AiOrchestrator.Data;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class StuckSagaSweeperTests : IDisposable
{
    private static readonly AutoSuggestionOptions DefaultOptions = new()
    {
        StuckReconcilingRetryAfterMinutes = 2,
        StuckReconcilingFailAfterMinutes = 30,
        StuckStepSweepAfterMinutes = 15,
        MaxReconcileTransientFailuresBeforeEscalate = 20,
        StuckReconcilingEscalateAfterMinutes = 120,
        ReconcileTransientBackoffBaseSeconds = 30,
        ReconcileTransientBackoffMaxSeconds = 900,
        ReconcileUnknownRedriveAfterMinutes = 15,
        MaxReconcileUnknownRedriveAttempts = 10,
        ReconcileUnknownBackoffBaseSeconds = 300,
        ReconcileUnknownBackoffMaxSeconds = 3600
    };

    [Fact]
    public void Plan_retries_stale_reconciling_saga()
    {
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.ReconcileRetry, actions[0].Type);
    }

    [Fact]
    public void Plan_marks_critically_stale_reconciling_saga_as_final_reconcile_candidate()
    {
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromMinutes(45));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.FinalReconcileCandidate, actions[0].Type);
        Assert.Contains("abandoned", actions[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_ignores_recent_reconciling_saga()
    {
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Empty(actions);
    }

    [Fact]
    public void Plan_skips_reconciling_saga_when_backoff_not_elapsed()
    {
        var now = DateTimeOffset.UtcNow;
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromMinutes(5));
        saga.ReconcileTransientFailureCount = 2;
        saga.LastReconcileAttemptAt = now - TimeSpan.FromSeconds(10);

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Empty(actions);
    }

    [Fact]
    public void Plan_retries_reconciling_saga_when_backoff_elapsed()
    {
        var now = DateTimeOffset.UtcNow;
        var saga = Saga(SagaProcessState.Reconciling, TimeSpan.FromMinutes(5));
        saga.ReconcileTransientFailureCount = 2;
        saga.LastReconcileAttemptAt = now - TimeSpan.FromMinutes(5);

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.ReconcileRetry, actions[0].Type);
    }

    [Fact]
    public void Plan_uses_reconciling_since_at_not_updated_at_for_fail_after()
    {
        var now = DateTimeOffset.UtcNow;
        // Simulate the bug: many retries refreshed UpdatedAt recently, but the saga has been Reconciling for a long time.
        var saga = new TicketSuggestionSaga
        {
            CorrelationId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            CurrentState = SagaProcessState.Reconciling,
            UpdatedAt = now - TimeSpan.FromMinutes(5), // recent (would have hidden the age with old code)
            ReconcilingSinceAt = now - TimeSpan.FromMinutes(45),
            CreatedAt = now - TimeSpan.FromMinutes(50)
        };

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.FinalReconcileCandidate, actions[0].Type);
    }

    [Fact]
    public void Plan_does_not_reset_fail_after_clock_when_updated_at_is_recent()
    {
        var now = DateTimeOffset.UtcNow;
        // Another simulation of the reset bug: UpdatedAt is "new" after retries, but ReconcilingSinceAt proves long stuck time.
        var saga = new TicketSuggestionSaga
        {
            CorrelationId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            CurrentState = SagaProcessState.Reconciling,
            UpdatedAt = now - TimeSpan.FromMinutes(1),
            ReconcilingSinceAt = now - TimeSpan.FromMinutes(40),
            CreatedAt = now - TimeSpan.FromMinutes(50)
        };

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.FinalReconcileCandidate, actions[0].Type);
    }

    [Fact]
    public void ComputeBackoffDelay_caps_at_max_seconds()
    {
        var delay = ReconcileTransientTracker.ComputeBackoffDelay(50, DefaultOptions);

        Assert.Equal(TimeSpan.FromSeconds(DefaultOptions.ReconcileTransientBackoffMaxSeconds), delay);
    }

    [Fact]
    public void Plan_sweeps_stuck_generating_saga()
    {
        var saga = Saga(SagaProcessState.GeneratingSuggestion, TimeSpan.FromMinutes(20));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.StuckStepSweep, actions[0].Type);
    }

    [Fact]
    public void Plan_sweeps_stuck_applying_saga()
    {
        var saga = Saga(SagaProcessState.ApplyingSuggestion, TimeSpan.FromMinutes(20));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.StuckStepSweep, actions[0].Type);
    }

    [Fact]
    public void Plan_ignores_recent_generating_saga()
    {
        var saga = Saga(SagaProcessState.GeneratingSuggestion, TimeSpan.FromMinutes(2));
        var now = DateTimeOffset.UtcNow;

        var actions = StuckSagaSweepPlanner.Plan([saga], EmptyReconciliationItems(), DefaultOptions, now);

        Assert.Empty(actions);
    }

    [Fact]
    public async Task SweepAsync_retries_abandon_candidate_when_reconcile_client_throws()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.Reconciling,
                TicketVersionAtStart = 1,
                CreatedAt = staleAt,
                UpdatedAt = staleAt
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        var reconcileClient = new ThrowingReconcileClient(new HttpRequestException("503"));
        var sweeper = CreateSweeper(publish.Object, reconcileClient);

        var result = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.StuckCount);
        Assert.Equal(1, result.ReconcileRetried);
        Assert.Equal(0, result.Abandoned);
        Assert.Equal(0, result.Escalated);
        publish.Verify(
            x => x.Publish(It.Is<IReconcileSweep>(m => m.SagaId == sagaId), It.IsAny<CancellationToken>()),
            Times.Once);
        publish.Verify(
            x => x.Publish(It.IsAny<IReconcileAbandon>(), It.IsAny<CancellationToken>()),
            Times.Never);
        publish.Verify(
            x => x.Publish(It.IsAny<IReconcileEscalate>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await using var verifyDb = CreateDbContext();
        var saga = await verifyDb.TicketSuggestionSagas.SingleAsync(s => s.CorrelationId == sagaId);
        Assert.Equal(1, saga.ReconcileTransientFailureCount);
        Assert.NotNull(saga.LastReconcileAttemptAt);
    }

    [Fact]
    public async Task SweepAsync_escalates_when_transient_failures_exceed_threshold()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.Reconciling,
                TicketVersionAtStart = 1,
                ReconcileTransientFailureCount = DefaultOptions.MaxReconcileTransientFailuresBeforeEscalate - 1,
                ReconcilingSinceAt = staleAt,
                CreatedAt = staleAt,
                UpdatedAt = staleAt
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        var reconcileClient = new ThrowingReconcileClient(new HttpRequestException("503"));
        var sweeper = CreateSweeper(publish.Object, reconcileClient);

        var result = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.Abandoned);
        Assert.Equal(1, result.Escalated);
        Assert.Equal(0, result.ReconcileRetried);
        publish.Verify(
            x => x.Publish(It.Is<IReconcileEscalate>(m => m.SagaId == sagaId), It.IsAny<CancellationToken>()),
            Times.Once);
        publish.Verify(
            x => x.Publish(It.IsAny<IReconcileAbandon>(), It.IsAny<CancellationToken>()),
            Times.Never);
        publish.Verify(
            x => x.Publish(It.IsAny<IReconcileSweep>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SweepAsync_recovers_via_reconcile_when_already_applied_by_same_job()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.Reconciling,
                TicketVersionAtStart = 1,
                ReconcileTransientFailureCount = 5,
                ReconcilingSinceAt = staleAt,
                CreatedAt = staleAt,
                UpdatedAt = staleAt
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        var reconcile = new AutoSuggestionReconcileResult(
            TestTicketIds.Default,
            jobId,
            AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob,
            null,
            TicketStatus.New,
            1,
            false,
            false);
        var reconcileClient = new StubReconcileClient(reconcile);
        var sweeper = CreateSweeper(publish.Object, reconcileClient);

        var result = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.ReconcileRetried);
        Assert.Equal(0, result.Abandoned);
        Assert.Equal(0, result.Escalated);
        publish.Verify(
            x => x.Publish(It.Is<IReconcileSweep>(m => m.SagaId == sagaId), It.IsAny<CancellationToken>()),
            Times.Once);
        publish.Verify(
            x => x.Publish(It.IsAny<IReconcileAbandon>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await using var verifyDb = CreateDbContext();
        var saga = await verifyDb.TicketSuggestionSagas.SingleAsync(s => s.CorrelationId == sagaId);
        Assert.Equal(0, saga.ReconcileTransientFailureCount);
        Assert.Null(saga.ReconcilingSinceAt);
    }

    [Fact]
    public async Task Store_records_transient_failure_and_persists_outside_saga_tx()
    {
        var sagaId = Guid.NewGuid();
        var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = Guid.NewGuid(),
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.Reconciling,
                ReconcileTransientFailureCount = 3,
                ReconcilingSinceAt = staleAt,
                CreatedAt = staleAt,
                UpdatedAt = staleAt
            });
            await db.SaveChangesAsync();
        }

        // Build provider that gives the store a separate DbContext scope (simulates activity tx rollback scenario)
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDbContext());
        services.AddScoped<ISagaReconcileFailureStore>(sp =>
            new SagaReconcileFailureStore(sp, NullLogger<SagaReconcileFailureStore>.Instance));
        var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<ISagaReconcileFailureStore>();
        var now = DateTimeOffset.UtcNow;
        await store.RecordTransientFailureAsync(sagaId, now);

        // Verify persisted via independent context
        await using var verify = CreateDbContext();
        var persisted = await verify.TicketSuggestionSagas.SingleAsync(s => s.CorrelationId == sagaId);
        Assert.Equal(4, persisted.ReconcileTransientFailureCount);
        Assert.NotNull(persisted.LastReconcileAttemptAt);
        Assert.True(persisted.LastReconcileAttemptAt >= now - TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Plan_redrives_reconcile_unknown_when_backoff_elapsed_and_under_limit()
    {
        var now = DateTimeOffset.UtcNow;
        var sagaId = Guid.NewGuid();
        var saga = Saga(SagaProcessState.ReconcileUnknown, TimeSpan.FromMinutes(20));
        saga.CorrelationId = sagaId;
        var items = new Dictionary<Guid, SagaReconciliationItem>
        {
            [sagaId] = new()
            {
                SagaId = sagaId,
                TicketId = saga.TicketId,
                JobId = saga.JobId,
                Reason = "transient",
                CreatedAt = now - TimeSpan.FromMinutes(20),
                AttemptCount = 0,
                LastAttemptAt = now - TimeSpan.FromMinutes(10)
            }
        };

        var actions = StuckSagaSweepPlanner.Plan([saga], items, DefaultOptions, now);

        Assert.Single(actions);
        Assert.Equal(StuckSagaSweepPlanner.SweepActionType.ReconcileUnknownRedrive, actions[0].Type);
    }

    [Fact]
    public void Plan_skips_reconcile_unknown_when_attempt_limit_exhausted()
    {
        var now = DateTimeOffset.UtcNow;
        var sagaId = Guid.NewGuid();
        var saga = Saga(SagaProcessState.ReconcileUnknown, TimeSpan.FromMinutes(20));
        saga.CorrelationId = sagaId;
        var items = new Dictionary<Guid, SagaReconciliationItem>
        {
            [sagaId] = new()
            {
                SagaId = sagaId,
                TicketId = saga.TicketId,
                JobId = saga.JobId,
                Reason = "transient",
                CreatedAt = now - TimeSpan.FromMinutes(20),
                AttemptCount = DefaultOptions.MaxReconcileUnknownRedriveAttempts,
                LastAttemptAt = now - TimeSpan.FromHours(2)
            }
        };

        var actions = StuckSagaSweepPlanner.Plan([saga], items, DefaultOptions, now);

        Assert.Empty(actions);
    }

    [Fact]
    public void Plan_skips_reconcile_unknown_when_backoff_not_elapsed()
    {
        var now = DateTimeOffset.UtcNow;
        var sagaId = Guid.NewGuid();
        var saga = Saga(SagaProcessState.ReconcileUnknown, TimeSpan.FromMinutes(20));
        saga.CorrelationId = sagaId;
        var items = new Dictionary<Guid, SagaReconciliationItem>
        {
            [sagaId] = new()
            {
                SagaId = sagaId,
                TicketId = saga.TicketId,
                JobId = saga.JobId,
                Reason = "transient",
                CreatedAt = now - TimeSpan.FromMinutes(20),
                AttemptCount = 1,
                LastAttemptAt = now - TimeSpan.FromSeconds(30)
            }
        };

        var actions = StuckSagaSweepPlanner.Plan([saga], items, DefaultOptions, now);

        Assert.Empty(actions);
    }

    [Fact]
    public async Task SweepAsync_auto_redrives_reconcile_unknown_saga()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var parkedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.ReconcileUnknown,
                TicketVersionAtStart = 1,
                CreatedAt = parkedAt,
                UpdatedAt = parkedAt
            });
            db.SagaReconciliationItems.Add(new SagaReconciliationItem
            {
                SagaId = sagaId,
                TicketId = TestTicketIds.Default,
                JobId = jobId,
                Reason = "escalated",
                CreatedAt = parkedAt,
                AttemptCount = 1,
                LastAttemptAt = parkedAt - TimeSpan.FromMinutes(10)
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        var reconcileClient = new ThrowingReconcileClient(new HttpRequestException("503"));
        var sweeper = CreateSweeper(publish.Object, reconcileClient);

        var result = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(1, result.UnknownRedriven);
        publish.Verify(
            x => x.Publish(It.Is<IReconcileRedrive>(m => m.SagaId == sagaId), It.IsAny<CancellationToken>()),
            Times.Once);
        publish.Verify(
            x => x.Publish(It.IsAny<IReconcileSweep>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Plan_skips_reconcile_unknown_immediately_after_scheduled_attempt()
    {
        var now = DateTimeOffset.UtcNow;
        var sagaId = Guid.NewGuid();
        var saga = Saga(SagaProcessState.ReconcileUnknown, TimeSpan.FromMinutes(20));
        saga.CorrelationId = sagaId;
        var items = new Dictionary<Guid, SagaReconciliationItem>
        {
            [sagaId] = new()
            {
                SagaId = sagaId,
                TicketId = saga.TicketId,
                JobId = saga.JobId,
                Reason = "transient",
                CreatedAt = now - TimeSpan.FromMinutes(20),
                AttemptCount = 1,
                LastAttemptAt = now - TimeSpan.FromSeconds(30)
            }
        };

        var actions = StuckSagaSweepPlanner.Plan([saga], items, DefaultOptions, now);

        Assert.Empty(actions);
    }

    [Fact]
    public async Task SweepAsync_updates_reconciliation_item_when_scheduling_auto_redrive()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var parkedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.ReconcileUnknown,
                TicketVersionAtStart = 1,
                CreatedAt = parkedAt,
                UpdatedAt = parkedAt
            });
            db.SagaReconciliationItems.Add(new SagaReconciliationItem
            {
                SagaId = sagaId,
                TicketId = TestTicketIds.Default,
                JobId = jobId,
                Reason = "escalated",
                CreatedAt = parkedAt,
                AttemptCount = 0,
                LastAttemptAt = parkedAt - TimeSpan.FromMinutes(10)
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        var reconcileClient = new ThrowingReconcileClient(new HttpRequestException("503"));
        var sweeper = CreateSweeper(publish.Object, reconcileClient);

        await sweeper.SweepAsync(CancellationToken.None);

        await using var verifyDb = CreateDbContext();
        var item = await verifyDb.SagaReconciliationItems.SingleAsync(x => x.SagaId == sagaId);
        Assert.Equal(1, item.AttemptCount);
        Assert.NotNull(item.LastAttemptAt);
    }

    [Fact]
    public async Task SweepAsync_backfills_orphan_reconcile_unknown_and_can_schedule_redrive()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var parkedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.ReconcileUnknown,
                FailureReason = "legacy orphan",
                TicketVersionAtStart = 1,
                CreatedAt = parkedAt,
                UpdatedAt = parkedAt
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        var sweeper = CreateSweeper(publish.Object, new ThrowingReconcileClient(new HttpRequestException("503")));

        var result = await sweeper.SweepAsync(CancellationToken.None);

        await using var verifyDb = CreateDbContext();
        var item = await verifyDb.SagaReconciliationItems.SingleAsync(x => x.SagaId == sagaId);
        Assert.Equal("legacy orphan", item.Reason);
        Assert.Equal(1, result.UnknownRedriven);
        Assert.Equal(1, item.AttemptCount);
        publish.Verify(
            x => x.Publish(It.Is<IReconcileRedrive>(m => m.SagaId == sagaId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SweepAsync_does_not_publish_second_auto_redrive_within_backoff()
    {
        var sagaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var parkedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        var justScheduled = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20);
        await using (var db = CreateDbContext())
        {
            db.TicketSuggestionSagas.Add(new TicketSuggestionSaga
            {
                CorrelationId = sagaId,
                JobId = jobId,
                TicketId = TestTicketIds.Default,
                CurrentState = SagaProcessState.ReconcileUnknown,
                TicketVersionAtStart = 1,
                CreatedAt = parkedAt,
                UpdatedAt = parkedAt
            });
            db.SagaReconciliationItems.Add(new SagaReconciliationItem
            {
                SagaId = sagaId,
                TicketId = TestTicketIds.Default,
                JobId = jobId,
                Reason = "escalated",
                CreatedAt = parkedAt,
                AttemptCount = 1,
                LastAttemptAt = justScheduled
            });
            await db.SaveChangesAsync();
        }

        var publish = new Mock<IPublishEndpoint>();
        var sweeper = CreateSweeper(publish.Object, new ThrowingReconcileClient(new HttpRequestException("503")));

        var result = await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.UnknownRedriven);
        publish.Verify(
            x => x.Publish(It.IsAny<IReconcileRedrive>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose() => _connection.Dispose();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public StuckSagaSweeperTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    private OrchestratorDbContext CreateDbContext() => new(_options);

    private StuckSagaSweeperService CreateSweeper(
        IPublishEndpoint publish,
        ITicketSuggestionReconcileClient reconcileClient)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDbContext());
        services.AddScoped(_ => publish);
        services.AddScoped(_ => reconcileClient);
        services.AddScoped<ISagaReconcileFailureStore>(sp =>
            new SagaReconcileFailureStore(sp, NullLogger<SagaReconcileFailureStore>.Instance));
        services.AddScoped<ISagaReconciliationQueue>(sp =>
            new SagaReconciliationQueue(sp.GetRequiredService<OrchestratorDbContext>()));
        var provider = services.BuildServiceProvider();

        return new StuckSagaSweeperService(
            provider,
            Microsoft.Extensions.Options.Options.Create(DefaultOptions),
            NullLogger<StuckSagaSweeperService>.Instance);
    }

    private sealed class ThrowingReconcileClient(Exception exception) : ITicketSuggestionReconcileClient
    {
        public Task<AutoSuggestionReconcileResult> ReconcileAsync(
            string ticketId,
            Guid jobId,
            long? expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AutoSuggestionReconcileResult>(exception);
    }

    private sealed class StubReconcileClient(AutoSuggestionReconcileResult result) : ITicketSuggestionReconcileClient
    {
        public Task<AutoSuggestionReconcileResult> ReconcileAsync(
            string ticketId,
            Guid jobId,
            long? expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static Dictionary<Guid, SagaReconciliationItem> EmptyReconciliationItems() => [];

    private static TicketSuggestionSaga Saga(string state, TimeSpan age)
    {
        var now = DateTimeOffset.UtcNow;
        var created = now - age;
        var saga = new TicketSuggestionSaga
        {
            CorrelationId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            CurrentState = state,
            UpdatedAt = created,
            CreatedAt = created
        };
        if (state == SagaProcessState.Reconciling)
        {
            // Default: anchor = entry time for tests that don't care about the bug scenario
            saga.ReconcilingSinceAt = created;
        }
        return saga;
    }
}
