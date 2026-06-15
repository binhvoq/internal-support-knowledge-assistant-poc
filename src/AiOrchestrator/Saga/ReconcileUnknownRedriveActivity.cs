using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Telemetry;

namespace SupportPoc.AiOrchestrator.Saga;

/// <summary>
/// Redrive reconcile from ReconcileUnknown — catches transient HTTP errors and keeps saga parked
/// instead of faulting the message (which would risk incorrect Failed transitions).
/// </summary>
public sealed class ReconcileUnknownRedriveActivity(
    ITicketSuggestionReconcileClient reconcileClient,
    IAiGenerationAttemptReader attemptReader,
    IOptions<AutoSuggestionOptions> options,
    ILogger<TicketSuggestionStateMachine> logger,
    IServiceProvider serviceProvider,
    ISagaReconcileFailureStore failureStore,
    ISagaReconciliationQueue reconciliationQueue) : IStateMachineActivity<TicketSuggestionSaga>
{
    public void Probe(ProbeContext context) => context.CreateScope("reconcile-unknown-redrive");

    public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);

    public async Task Execute(BehaviorContext<TicketSuggestionSaga> context, IBehavior<TicketSuggestionSaga> next)
    {
        await RedriveAsync(context);
        await next.Execute(context).ConfigureAwait(false);
    }

    public async Task Execute<T>(BehaviorContext<TicketSuggestionSaga, T> context, IBehavior<TicketSuggestionSaga, T> next)
        where T : class
    {
        await RedriveAsync(context);
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

    private async Task RedriveAsync(BehaviorContext<TicketSuggestionSaga> context)
    {
        var saga = context.Saga;
        saga.PendingReconcileAction = null;
        var now = DateTimeOffset.UtcNow;

        try
        {
            var (outcome, reconcile) = await ReconcileTicketSuggestionActivity.ResolveReconcileOutcomeAsync(
                saga,
                options.Value,
                reconcileClient,
                attemptReader,
                context.CancellationToken);

            ReconcileTransientTracker.RecordSuccess(saga, now);
            await failureStore.RecordSuccessAsync(saga.CorrelationId, now, context.CancellationToken);

            SagaReconcileTelemetry.TrackDecision(
                serviceProvider.TryGetTelemetryClient(),
                reconcile.Decision,
                outcome.Action,
                saga.CorrelationId,
                saga.TicketId);

            logger.LogInformation(
                "ReconcileUnknown redrive: decision={Decision} action={Action} SagaId={SagaId} TicketId={TicketId}",
                reconcile.Decision,
                outcome.Action,
                saga.CorrelationId,
                saga.TicketId);

            ReconcileTicketSuggestionActivity.ApplyOutcome(context, outcome);
            await reconciliationQueue.MarkResolvedAsync(saga.CorrelationId, outcome.Action, now, context.CancellationToken);
            SagaReconcileTelemetry.TrackUnknownRecovered(
                serviceProvider.TryGetTelemetryClient(),
                saga.CorrelationId,
                saga.TicketId,
                outcome.Action);
        }
        catch (Exception ex)
        {
            ReconcileTransientTracker.RecordTransientFailure(saga, now);
            await failureStore.RecordTransientFailureAsync(saga.CorrelationId, now, context.CancellationToken);
            // Auto attempt count was incremented at schedule time; do not increment again on transient failure.
            saga.PendingReconcileAction = null;
            saga.UpdatedAt = now;

            SagaReconcileTelemetry.TrackHttpFailure(
                serviceProvider.TryGetTelemetryClient(),
                saga.CorrelationId,
                saga.TicketId,
                ex.GetType().Name);
            SagaReconcileTelemetry.TrackUnknownStayedParked(
                serviceProvider.TryGetTelemetryClient(),
                saga.CorrelationId,
                saga.TicketId,
                ex.GetType().Name);

            logger.LogWarning(
                ex,
                "ReconcileUnknown redrive: TicketService call failed; staying parked SagaId={SagaId} TicketId={TicketId} FailureCount={FailureCount}",
                saga.CorrelationId,
                saga.TicketId,
                saga.ReconcileTransientFailureCount);
        }
    }

    internal static bool IsTerminalReconcileAction(string action) =>
        action is ReconcileActions.Complete or ReconcileActions.Discard or ReconcileActions.Fail;
}
