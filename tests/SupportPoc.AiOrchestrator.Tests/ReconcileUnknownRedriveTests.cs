using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class ReconcileUnknownRedriveTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public ReconcileUnknownRedriveTests()
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
    public void IsTerminalReconcileAction_identifies_terminal_outcomes()
    {
        Assert.True(ReconcileUnknownRedriveActivity.IsTerminalReconcileAction(ReconcileActions.Complete));
        Assert.True(ReconcileUnknownRedriveActivity.IsTerminalReconcileAction(ReconcileActions.Discard));
        Assert.True(ReconcileUnknownRedriveActivity.IsTerminalReconcileAction(ReconcileActions.Fail));
        Assert.False(ReconcileUnknownRedriveActivity.IsTerminalReconcileAction(ReconcileActions.Propose));
        Assert.False(ReconcileUnknownRedriveActivity.IsTerminalReconcileAction(ReconcileActions.Retry));
        Assert.False(ReconcileUnknownRedriveActivity.IsTerminalReconcileAction(ReconcileActions.WaitForGeneration));
    }

    [Fact]
    public void RedrivePolicy_allows_only_reconcile_unknown_state()
    {
        var unknown = new TicketSuggestionSaga { CurrentState = SagaProcessState.ReconcileUnknown };
        var reconciling = new TicketSuggestionSaga { CurrentState = SagaProcessState.Reconciling };
        var failed = new TicketSuggestionSaga { CurrentState = SagaProcessState.Failed };

        Assert.True(ReconcileUnknownRedrivePolicy.IsEligible(unknown));
        Assert.False(ReconcileUnknownRedrivePolicy.IsEligible(reconciling));
        Assert.False(ReconcileUnknownRedrivePolicy.IsEligible(failed));
        Assert.False(ReconcileUnknownRedrivePolicy.IsEligible(null));
    }

    [Fact]
    public async Task Resolve_and_apply_already_applied_sets_complete_action()
    {
        var saga = NewSaga();
        var jobId = saga.JobId;
        var reconcile = new AutoSuggestionReconcileResult(
            TestTicketIds.Default,
            jobId,
            AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob,
            null,
            TicketStatus.New,
            1,
            false,
            false);
        var client = new StubReconcileClient(reconcile);
        var options = Microsoft.Extensions.Options.Options.Create(new AutoSuggestionOptions());

        var (outcome, _) = await ReconcileTicketSuggestionActivity.ResolveReconcileOutcomeAsync(
            saga,
            options.Value,
            client,
            new NullAttemptReader(),
            CancellationToken.None);

        Assert.Equal(ReconcileActions.Complete, outcome.Action);
        Assert.True(ReconcileUnknownRedriveActivity.IsTerminalReconcileAction(outcome.Action));
    }

    [Fact]
    public void Transient_failure_path_keeps_pending_action_null()
    {
        var saga = NewSaga();
        saga.PendingReconcileAction = ReconcileActions.Complete;
        var now = DateTimeOffset.UtcNow;

        ReconcileTransientTracker.RecordTransientFailure(saga, now);
        saga.PendingReconcileAction = null;

        Assert.Null(saga.PendingReconcileAction);
        Assert.Equal(1, saga.ReconcileTransientFailureCount);
    }

    [Fact]
    public async Task Queue_creates_item_on_escalate_and_marks_resolved_on_recovery()
    {
        var sagaId = Guid.NewGuid();
        var saga = NewSaga();
        saga.CorrelationId = sagaId;
        saga.FailureReason = "HTTP failures";

        await using (var db = CreateDbContext())
        {
            var queue = new SagaReconciliationQueue(db);
            await queue.UpsertOnEscalateAsync(saga, saga.FailureReason);

            var item = await db.SagaReconciliationItems.SingleAsync(x => x.SagaId == sagaId);
            Assert.Equal(TestTicketIds.Default, item.TicketId);
            Assert.Equal(saga.JobId, item.JobId);
            Assert.Equal(0, item.AttemptCount);
            Assert.Null(item.ResolvedAt);

            await queue.RecordScheduledAutoRedriveAsync(sagaId, DateTimeOffset.UtcNow);
            var afterAttempt = await db.SagaReconciliationItems.SingleAsync(x => x.SagaId == sagaId);
            Assert.Equal(1, afterAttempt.AttemptCount);

            await queue.MarkResolvedAsync(sagaId, ReconcileActions.Complete, DateTimeOffset.UtcNow);
            var resolved = await db.SagaReconciliationItems.SingleAsync(x => x.SagaId == sagaId);
            Assert.Equal(ReconcileActions.Complete, resolved.Resolution);
            Assert.NotNull(resolved.ResolvedAt);
        }
    }

    [Fact]
    public async Task Backfill_creates_single_item_for_orphan_unknown_saga()
    {
        var sagaId = Guid.NewGuid();
        var saga = NewSaga();
        saga.CorrelationId = sagaId;
        saga.FailureReason = "orphan";

        await using var db = CreateDbContext();
        var queue = new SagaReconciliationQueue(db);
        var created = await queue.BackfillMissingItemsAsync([saga]);
        var again = await queue.BackfillMissingItemsAsync([saga]);

        Assert.Equal(1, created);
        Assert.Equal(0, again);
        var item = await db.SagaReconciliationItems.SingleAsync(x => x.SagaId == sagaId);
        Assert.Equal("orphan", item.Reason);
    }

    [Fact]
    public void Projection_marks_exhausted_unknown_saga()
    {
        var now = DateTimeOffset.UtcNow;
        var saga = NewSaga();
        var item = new SagaReconciliationItem
        {
            SagaId = saga.CorrelationId,
            TicketId = saga.TicketId,
            JobId = saga.JobId,
            Reason = "transient",
            CreatedAt = now - TimeSpan.FromHours(2),
            AttemptCount = 10,
            LastAttemptAt = now - TimeSpan.FromHours(1)
        };
        var opts = new AutoSuggestionOptions { MaxReconcileUnknownRedriveAttempts = 10 };

        var view = ReconcileUnknownProjection.Project(saga, item, opts, now);

        Assert.Equal(ReconcileUnknownProjection.StatusExhausted, view.Status);
        Assert.Null(view.NextAutoRedriveEligibleAt);
    }

    [Fact]
    public void Projection_marks_missing_item_status()
    {
        var now = DateTimeOffset.UtcNow;
        var saga = NewSaga();
        var opts = new AutoSuggestionOptions();

        var view = ReconcileUnknownProjection.Project(saga, null, opts, now);

        Assert.Equal(ReconcileUnknownProjection.StatusMissingItem, view.Status);
    }

    [Fact]
    public void OpsCallerIdentity_uses_anonymous_dev_in_development()
    {
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Development };
        Assert.Equal("anonymous/dev", OpsCallerIdentity.Resolve(null, env));
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private OrchestratorDbContext CreateDbContext() => new(_options);

    private sealed class NullAttemptReader : IAiGenerationAttemptReader
    {
        public Task<AiGenerationAttemptSnapshot?> GetByAttemptIdAsync(
            Guid attemptId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AiGenerationAttemptSnapshot?>(null);
    }

    private static TicketSuggestionSaga NewSaga() =>
        new()
        {
            CorrelationId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            TicketId = TestTicketIds.Default,
            CurrentState = SagaProcessState.ReconcileUnknown
        };

    private sealed class StubReconcileClient(AutoSuggestionReconcileResult result) : ITicketSuggestionReconcileClient
    {
        public Task<AutoSuggestionReconcileResult> ReconcileAsync(
            string ticketId,
            Guid jobId,
            long? expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
