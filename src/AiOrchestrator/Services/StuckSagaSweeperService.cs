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
        var telemetry = scope.ServiceProvider.TryGetTelemetryClient();

        var opts = options.Value;
        var now = DateTimeOffset.UtcNow;
        var stuck = await db.TicketSuggestionSagas
            .Where(s => MonitoredStates.Contains(s.CurrentState))
            .ToListAsync(cancellationToken);

        var actions = StuckSagaSweepPlanner.Plan(stuck, opts, now);
        var result = new SagaSweepResult(stuck.Count);

        foreach (var action in actions)
        {
            var age = now - action.Saga.UpdatedAt;
            switch (action.Type)
            {
                case StuckSagaSweepPlanner.SweepActionType.AbandonCandidate:
                    await HandleAbandonCandidateAsync(action, reconcileClient, publish, result, telemetry, age, cancellationToken);
                    break;
                case StuckSagaSweepPlanner.SweepActionType.ReconcileRetry:
                    await publish.Publish<IReconcileSweep>(new ReconcileSweep(action.Saga.CorrelationId), cancellationToken);
                    result.ReconcileRetried++;
                    SagaReconcileTelemetry.TrackSweep(telemetry, "retry", action.Saga.CorrelationId, action.Saga.TicketId, age);
                    logger.LogInformation(
                        "Sweeper re-triggering reconcile SagaId={SagaId} TicketId={TicketId} AgeMinutes={AgeMinutes}",
                        action.Saga.CorrelationId,
                        action.Saga.TicketId,
                        age.TotalMinutes);
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
            }
        }

        SagaReconcileTelemetry.TrackStuckCount(telemetry, stuck.Count, result.ReconcileRetried, result.Abandoned);
        return result;
    }

    private async Task HandleAbandonCandidateAsync(
        StuckSagaSweepPlanner.SweepAction action,
        ITicketSuggestionReconcileClient reconcileClient,
        IPublishEndpoint publish,
        SagaSweepResult result,
        Microsoft.ApplicationInsights.TelemetryClient? telemetry,
        TimeSpan age,
        CancellationToken cancellationToken)
    {
        AutoSuggestionReconcileResult? reconcile = null;
        Exception? reconcileError = null;
        try
        {
            reconcile = await reconcileClient.ReconcileAsync(
                action.Saga.TicketId,
                action.Saga.JobId,
                action.Saga.TicketVersionAtStart,
                cancellationToken);
        }
        catch (Exception ex)
        {
            reconcileError = ex;
            logger.LogWarning(
                ex,
                "Sweeper final reconcile before abandon failed SagaId={SagaId} TicketId={TicketId}",
                action.Saga.CorrelationId,
                action.Saga.TicketId);
        }

        var resolved = StuckSagaAbandonPolicy.Decide(reconcile, reconcileError);
        if (resolved == StuckSagaAbandonPolicy.ResolvedAction.ReconcileSweep)
        {
            await publish.Publish<IReconcileSweep>(new ReconcileSweep(action.Saga.CorrelationId), cancellationToken);
            result.ReconcileRetried++;
            SagaReconcileTelemetry.TrackSweep(
                telemetry,
                "abandon-recovered",
                action.Saga.CorrelationId,
                action.Saga.TicketId,
                age);
            logger.LogInformation(
                "Sweeper recovered abandon candidate via reconcile SagaId={SagaId} Decision={Decision}",
                action.Saga.CorrelationId,
                reconcile?.Decision);
            return;
        }

        await publish.Publish<IReconcileAbandon>(
            new ReconcileAbandon(action.Saga.CorrelationId, action.Reason ?? "Abandoned"),
            cancellationToken);
        result.Abandoned++;
        SagaReconcileTelemetry.TrackSweep(telemetry, "abandon", action.Saga.CorrelationId, action.Saga.TicketId, age);
        logger.LogWarning(
            "Sweeper abandoning stuck saga SagaId={SagaId} TicketId={TicketId} AgeMinutes={AgeMinutes}",
            action.Saga.CorrelationId,
            action.Saga.TicketId,
            age.TotalMinutes);
    }

    internal sealed class SagaSweepResult(int stuckCount)
    {
        public int StuckCount { get; } = stuckCount;
        public int ReconcileRetried { get; set; }
        public int Abandoned { get; set; }
        public int StuckStepSwept { get; set; }
    }
}
