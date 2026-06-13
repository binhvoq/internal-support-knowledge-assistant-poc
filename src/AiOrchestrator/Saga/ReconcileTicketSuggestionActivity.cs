using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Telemetry;

namespace SupportPoc.AiOrchestrator.Saga;

public sealed class ReconcileTicketSuggestionActivity(
    ITicketSuggestionReconcileClient reconcileClient,
    IOptions<AutoSuggestionOptions> options,
    ILogger<TicketSuggestionStateMachine> logger,
    IServiceProvider serviceProvider,
    ISagaReconcileFailureStore failureStore) : IStateMachineActivity<TicketSuggestionSaga>
{
    private readonly ISagaReconcileFailureStore _failureStore = failureStore;
    public void Probe(ProbeContext context) => context.CreateScope("reconcile-ticket-suggestion");

    public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);

    public async Task Execute(BehaviorContext<TicketSuggestionSaga> context, IBehavior<TicketSuggestionSaga> next)
    {
        await ReconcileAsync(context);
        await next.Execute(context).ConfigureAwait(false);
    }

    public async Task Execute<T>(BehaviorContext<TicketSuggestionSaga, T> context, IBehavior<TicketSuggestionSaga, T> next)
        where T : class
    {
        await ReconcileAsync(context);
        await next.Execute(context).ConfigureAwait(false);
    }

    public Task Faulted<TException>(
        BehaviorExceptionContext<TicketSuggestionSaga, TException> context,
        IBehavior<TicketSuggestionSaga> next)
        where TException : Exception =>
        next.Faulted(context);

    public Task Faulted<T, TException>(
        BehaviorExceptionContext<TicketSuggestionSaga, T, TException> context,
        IBehavior<TicketSuggestionSaga, T> next)
        where T : class
        where TException : Exception =>
        next.Faulted(context);

    private async Task ReconcileAsync(BehaviorContext<TicketSuggestionSaga> context)
    {
        var saga = context.Saga;
        saga.PendingReconcileAction = null;
        var now = DateTimeOffset.UtcNow;
        ReconcileTransientTracker.EnsureReconcilingSince(saga, now);

        try
        {
        var (outcome, reconcile) = await ResolveReconcileOutcomeAsync(
            saga,
            options.Value,
            reconcileClient,
            context.CancellationToken);

        ReconcileTransientTracker.RecordSuccess(saga, now);
        await _failureStore.RecordSuccessAsync(saga.CorrelationId, now, context.CancellationToken);

        var telemetry = serviceProvider.TryGetTelemetryClient();
            SagaReconcileTelemetry.TrackDecision(
                telemetry,
                reconcile.Decision,
                outcome.Action,
                saga.CorrelationId,
                saga.TicketId);

            logger.LogInformation(
                "Reconcile: TicketService decision={Decision} action={Action} SagaId={SagaId} TicketId={TicketId}",
                reconcile.Decision,
                outcome.Action,
                saga.CorrelationId,
                saga.TicketId);

            ApplyOutcome(context, outcome);
        }
        catch (Exception ex)
        {
            ReconcileTransientTracker.RecordTransientFailure(saga, now);
            await _failureStore.RecordTransientFailureAsync(saga.CorrelationId, now, context.CancellationToken);
            SagaReconcileTelemetry.TrackHttpFailure(
                serviceProvider.TryGetTelemetryClient(),
                saga.CorrelationId,
                saga.TicketId,
                ex.GetType().Name);
            logger.LogWarning(
                ex,
                "Reconcile: TicketService call failed; message will be retried SagaId={SagaId} TicketId={TicketId} FailureCount={FailureCount}",
                saga.CorrelationId,
                saga.TicketId,
                saga.ReconcileTransientFailureCount);
            throw;
        }
    }

    internal static async Task<(ReconcilePlanner.Outcome Outcome, AutoSuggestionReconcileResult Reconcile)> ResolveReconcileOutcomeAsync(
        TicketSuggestionSaga saga,
        AutoSuggestionOptions options,
        ITicketSuggestionReconcileClient reconcileClient,
        CancellationToken cancellationToken)
    {
        var reconcile = await reconcileClient.ReconcileAsync(
            saga.TicketId,
            saga.JobId,
            saga.TicketVersionAtStart,
            cancellationToken);

        return (ReconcilePlanner.Decide(saga, options, reconcile), reconcile);
    }

    internal static void ApplyOutcome(BehaviorContext<TicketSuggestionSaga> context, ReconcilePlanner.Outcome outcome)
    {
        var saga = context.Saga;

        if (outcome.IncrementProposeRetry)
            saga.ProposeRetryCount++;

        if (outcome.StartNewGenerationAttempt)
        {
            saga.RetryCount++;
            TicketSuggestionActivities.StartNewAttempt(context);
        }

        if (outcome.DiscardReason is not null)
            saga.DiscardReason = outcome.DiscardReason;

        if (outcome.FailureReason is not null)
            saga.FailureReason = outcome.FailureReason;

        saga.PendingReconcileAction = outcome.Action;
        saga.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
