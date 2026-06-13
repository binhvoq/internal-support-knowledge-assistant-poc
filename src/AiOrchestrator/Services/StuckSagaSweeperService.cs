using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Telemetry;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class StuckSagaSweeperService(
    IServiceProvider serviceProvider,
    IOptions<AutoSuggestionOptions> options,
    ILogger<StuckSagaSweeperService> logger) : BackgroundService
{
    private static readonly string[] MonitoredStates =
    [
        SagaProcessState.Reconciling,
        SagaProcessState.ReconcileUnknown,
        SagaProcessState.GeneratingSuggestion,
        SagaProcessState.ApplyingSuggestion
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(15, options.Value.StuckReconcilingSweepIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Stuck saga sweeper iteration failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    internal async Task<SagaSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var reconcileClient = scope.ServiceProvider.GetRequiredService<ITicketSuggestionReconcileClient>();
        var failureStore = scope.ServiceProvider.GetRequiredService<ISagaReconcileFailureStore>();
        var reconciliationQueue = scope.ServiceProvider.GetRequiredService<ISagaReconciliationQueue>();
        var telemetry = scope.ServiceProvider.TryGetTelemetryClient();

        var opts = options.Value;
        var now = DateTimeOffset.UtcNow;
        var stuck = await db.TicketSuggestionSagas
            .Where(s => MonitoredStates.Contains(s.CurrentState))
            .ToListAsync(cancellationToken);

        var unknownSagas = stuck
            .Where(s => s.CurrentState == SagaProcessState.ReconcileUnknown)
            .ToList();
        if (unknownSagas.Count > 0)
            await reconciliationQueue.BackfillMissingItemsAsync(unknownSagas, cancellationToken);

        var unknownSagaIds = unknownSagas.Select(s => s.CorrelationId).ToList();
        var reconciliationItems = unknownSagaIds.Count == 0
            ? new Dictionary<Guid, SagaReconciliationItem>()
            : await db.SagaReconciliationItems
                .Where(x => unknownSagaIds.Contains(x.SagaId))
                .ToDictionaryAsync(x => x.SagaId, cancellationToken);

        TrackExhaustedUnknownSagas(telemetry, unknownSagas, reconciliationItems, opts);

        var actions = StuckSagaSweepPlanner.Plan(stuck, reconciliationItems, opts, now);
        var result = new SagaSweepResult(stuck.Count);

        foreach (var action in actions)
        {
            var age = action.Saga.CurrentState == SagaProcessState.Reconciling
                ? ReconcileTransientTracker.GetReconcilingAge(action.Saga, now)
                : (now - action.Saga.UpdatedAt);
            switch (action.Type)
            {
                case StuckSagaSweepPlanner.SweepActionType.FinalReconcileCandidate:
                    await HandleFinalReconcileCandidateAsync(
                        db,
                        action,
                        reconcileClient,
                        publish,
                        result,
                        telemetry,
                        failureStore,
                        opts,
                        now,
                        age,
                        cancellationToken);
                    break;
                case StuckSagaSweepPlanner.SweepActionType.ReconcileRetry:
                    await HandleReconcileRetryAsync(
                        db,
                        action,
                        publish,
                        result,
                        telemetry,
                        failureStore,
                        opts,
                        now,
                        age,
                        cancellationToken);
                    break;
                case StuckSagaSweepPlanner.SweepActionType.StuckStepSweep:
                    await publish.Publish<IStuckStepSweep>(new StuckStepSweep(action.Saga.CorrelationId), cancellationToken);
                    result.StuckStepSwept++;
                    SagaReconcileTelemetry.TrackSweep(telemetry, "stuck-step", action.Saga.CorrelationId, action.Saga.TicketId, age);
                    logger.LogWarning(
                        "Sweeper stuck-step sweep SagaId={SagaId} TicketId={TicketId} State={State} AgeMinutes={AgeMinutes}",
                        action.Saga.CorrelationId,
                        action.Saga.TicketId,
                        action.Saga.CurrentState,
                        age.TotalMinutes);
                    break;
                case StuckSagaSweepPlanner.SweepActionType.ReconcileUnknownRedrive:
                    await HandleReconcileUnknownRedriveAsync(
                        db,
                        action,
                        publish,
                        reconciliationQueue,
                        result,
                        telemetry,
                        failureStore,
                        opts,
                        now,
                        age,
                        reconciliationItems,
                        cancellationToken);
                    break;
            }
        }

        var exhaustedCount = unknownSagas.Count(s =>
            reconciliationItems.TryGetValue(s.CorrelationId, out var item)
            && item.ResolvedAt is null
            && item.AttemptCount >= Math.Max(1, opts.MaxReconcileUnknownRedriveAttempts));

        SagaReconcileTelemetry.TrackStuckCount(
            telemetry,
            stuck.Count,
            result.ReconcileRetried,
            result.Abandoned,
            result.Escalated,
            result.UnknownRedriven,
            exhaustedCount);
        return result;
    }

    private static void TrackExhaustedUnknownSagas(
        Microsoft.ApplicationInsights.TelemetryClient? telemetry,
        IReadOnlyList<TicketSuggestionSaga> unknownSagas,
        IReadOnlyDictionary<Guid, SagaReconciliationItem> reconciliationItems,
        AutoSuggestionOptions opts)
    {
        var maxAttempts = Math.Max(1, opts.MaxReconcileUnknownRedriveAttempts);
        foreach (var saga in unknownSagas)
        {
            if (!reconciliationItems.TryGetValue(saga.CorrelationId, out var item))
                continue;
            if (item.ResolvedAt is not null || item.AttemptCount < maxAttempts)
                continue;

            SagaReconcileTelemetry.TrackUnknownExhausted(
                telemetry,
                saga.CorrelationId,
                saga.TicketId,
                item.AttemptCount,
                maxAttempts);
        }
    }

    private async Task HandleReconcileUnknownRedriveAsync(
        OrchestratorDbContext db,
        StuckSagaSweepPlanner.SweepAction action,
        IPublishEndpoint publish,
        ISagaReconciliationQueue reconciliationQueue,
        SagaSweepResult result,
        Microsoft.ApplicationInsights.TelemetryClient? telemetry,
        ISagaReconcileFailureStore failureStore,
        AutoSuggestionOptions opts,
        DateTimeOffset now,
        TimeSpan age,
        IReadOnlyDictionary<Guid, SagaReconciliationItem> reconciliationItems,
        CancellationToken cancellationToken)
    {
        var saga = await LoadSagaAsync(db, action.Saga.CorrelationId, cancellationToken);
        if (saga is null || saga.CurrentState != SagaProcessState.ReconcileUnknown)
            return;

        ReconcileTransientTracker.RecordScheduledAttempt(saga, now);
        await failureStore.RecordScheduledAttemptAsync(saga.CorrelationId, now, cancellationToken);
        await reconciliationQueue.RecordScheduledAutoRedriveAsync(saga.CorrelationId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var attemptCount = reconciliationItems.TryGetValue(saga.CorrelationId, out var beforeItem)
            ? beforeItem.AttemptCount + 1
            : 1;

        await publish.Publish<IReconcileRedrive>(new ReconcileRedrive(saga.CorrelationId), cancellationToken);
        result.UnknownRedriven++;
        SagaReconcileTelemetry.TrackSweep(telemetry, "unknown-redrive", saga.CorrelationId, saga.TicketId, age);
        SagaReconcileTelemetry.TrackUnknownAutoRedrive(
            telemetry,
            saga.CorrelationId,
            saga.TicketId,
            attemptCount,
            Math.Max(1, opts.MaxReconcileUnknownRedriveAttempts));
        logger.LogInformation(
            "Sweeper auto-redriving ReconcileUnknown saga SagaId={SagaId} TicketId={TicketId} AttemptCount={AttemptCount} AgeMinutes={AgeMinutes}",
            saga.CorrelationId,
            saga.TicketId,
            attemptCount,
            age.TotalMinutes);
    }

    private async Task HandleReconcileRetryAsync(
        OrchestratorDbContext db,
        StuckSagaSweepPlanner.SweepAction action,
        IPublishEndpoint publish,
        SagaSweepResult result,
        Microsoft.ApplicationInsights.TelemetryClient? telemetry,
        ISagaReconcileFailureStore failureStore,
        AutoSuggestionOptions opts,
        DateTimeOffset now,
        TimeSpan age,
        CancellationToken cancellationToken)
    {
        var saga = await LoadSagaAsync(db, action.Saga.CorrelationId, cancellationToken);
        if (saga is null)
            return;

        ReconcileTransientTracker.RecordScheduledAttempt(saga, now);
        await failureStore.RecordScheduledAttemptAsync(saga.CorrelationId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await publish.Publish<IReconcileSweep>(new ReconcileSweep(saga.CorrelationId), cancellationToken);
        result.ReconcileRetried++;
        SagaReconcileTelemetry.TrackSweep(telemetry, "retry", saga.CorrelationId, saga.TicketId, age);
        logger.LogInformation(
            "Sweeper re-triggering reconcile SagaId={SagaId} TicketId={TicketId} AgeMinutes={AgeMinutes} FailureCount={FailureCount}",
            saga.CorrelationId,
            saga.TicketId,
            age.TotalMinutes,
            saga.ReconcileTransientFailureCount);
    }

    private async Task HandleFinalReconcileCandidateAsync(
        OrchestratorDbContext db,
        StuckSagaSweepPlanner.SweepAction action,
        ITicketSuggestionReconcileClient reconcileClient,
        IPublishEndpoint publish,
        SagaSweepResult result,
        Microsoft.ApplicationInsights.TelemetryClient? telemetry,
        ISagaReconcileFailureStore failureStore,
        AutoSuggestionOptions opts,
        DateTimeOffset now,
        TimeSpan age,
        CancellationToken cancellationToken)
    {
        var saga = await LoadSagaAsync(db, action.Saga.CorrelationId, cancellationToken);
        if (saga is null)
            return;

        AutoSuggestionReconcileResult? reconcile = null;
        Exception? reconcileError = null;
        try
        {
            reconcile = await reconcileClient.ReconcileAsync(
                saga.TicketId,
                saga.JobId,
                saga.TicketVersionAtStart,
                cancellationToken);
            ReconcileTransientTracker.RecordSuccess(saga, now);
            await failureStore.RecordSuccessAsync(saga.CorrelationId, now, cancellationToken);
        }
        catch (Exception ex)
        {
            reconcileError = ex;
            ReconcileTransientTracker.RecordTransientFailure(saga, now);
            await failureStore.RecordTransientFailureAsync(saga.CorrelationId, now, cancellationToken);
            logger.LogWarning(
                ex,
                "Sweeper final reconcile before abandon failed SagaId={SagaId} TicketId={TicketId} FailureCount={FailureCount}",
                saga.CorrelationId,
                saga.TicketId,
                saga.ReconcileTransientFailureCount);
        }

        await db.SaveChangesAsync(cancellationToken);

        var resolved = StuckSagaAbandonPolicy.Decide(reconcile, reconcileError, saga, opts, now);
        switch (resolved)
        {
            case StuckSagaAbandonPolicy.ResolvedAction.ReconcileSweep:
                await publish.Publish<IReconcileSweep>(new ReconcileSweep(saga.CorrelationId), cancellationToken);
                result.ReconcileRetried++;
                if (reconcileError is not null)
                {
                    SagaReconcileTelemetry.TrackSweep(
                        telemetry,
                        "reconcile-retry-transient",
                        saga.CorrelationId,
                        saga.TicketId,
                        age);
                    logger.LogWarning(
                        "Sweeper final reconcile failed; keeping saga in Reconciling for retry SagaId={SagaId} TicketId={TicketId} FailureCount={FailureCount}",
                        saga.CorrelationId,
                        saga.TicketId,
                        saga.ReconcileTransientFailureCount);
                }
                else
                {
                    SagaReconcileTelemetry.TrackSweep(
                        telemetry,
                        "abandon-recovered",
                        saga.CorrelationId,
                        saga.TicketId,
                        age);
                    logger.LogInformation(
                        "Sweeper recovered abandon candidate via reconcile SagaId={SagaId} Decision={Decision}",
                        saga.CorrelationId,
                        reconcile?.Decision);
                }

                break;

            case StuckSagaAbandonPolicy.ResolvedAction.EscalateUnknown:
                var escalateReason =
                    "Could not confirm ticket state after prolonged reconcile failures. Check TicketService availability and ticket state manually.";
                await publish.Publish<IReconcileEscalate>(
                    new ReconcileEscalate(saga.CorrelationId, escalateReason),
                    cancellationToken);
                result.Escalated++;
                SagaReconcileTelemetry.TrackSweep(telemetry, "reconcile-escalated-unknown", saga.CorrelationId, saga.TicketId, age);
                SagaReconcileTelemetry.TrackEscalatedToUnknown(telemetry, saga.CorrelationId, saga.TicketId, escalateReason);
                logger.LogWarning(
                    "Sweeper escalating saga to ReconcileUnknown SagaId={SagaId} TicketId={TicketId} FailureCount={FailureCount} AgeMinutes={AgeMinutes}",
                    saga.CorrelationId,
                    saga.TicketId,
                    saga.ReconcileTransientFailureCount,
                    age.TotalMinutes);
                break;

            default:
                await publish.Publish<IReconcileAbandon>(
                    new ReconcileAbandon(saga.CorrelationId, action.Reason ?? "Abandoned"),
                    cancellationToken);
                result.Abandoned++;
                SagaReconcileTelemetry.TrackSweep(telemetry, "abandon", saga.CorrelationId, saga.TicketId, age);
                logger.LogWarning(
                    "Sweeper abandoning stuck saga SagaId={SagaId} TicketId={TicketId} AgeMinutes={AgeMinutes}",
                    saga.CorrelationId,
                    saga.TicketId,
                    age.TotalMinutes);
                break;
        }
    }

    private static async Task<TicketSuggestionSaga?> LoadSagaAsync(
        OrchestratorDbContext db,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        await db.TicketSuggestionSagas
            .FirstOrDefaultAsync(s => s.CorrelationId == correlationId, cancellationToken);

    internal sealed class SagaSweepResult(int stuckCount)
    {
        public int StuckCount { get; } = stuckCount;
        public int ReconcileRetried { get; set; }
        public int Abandoned { get; set; }
        public int Escalated { get; set; }
        public int StuckStepSwept { get; set; }
        public int UnknownRedriven { get; set; }
    }
}
