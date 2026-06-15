using MassTransit;
using Microsoft.Extensions.Logging;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Services;

namespace SupportPoc.AiOrchestrator.Saga;

/// <summary>
/// Dong attempt active hien tai (neu co) truoc khi saga tao AttemptId moi va gui generation request.
/// </summary>
public sealed class PrepareGenerationRetryActivity(
    IAiGenerationAttemptReader attemptReader,
    IAiGenerationAttemptLifecycle attemptLifecycle,
    ILogger<TicketSuggestionStateMachine> logger) : IStateMachineActivity<TicketSuggestionSaga>
{
    public void Probe(ProbeContext context) => context.CreateScope("prepare-generation-retry");

    public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);

    public async Task Execute(BehaviorContext<TicketSuggestionSaga> context, IBehavior<TicketSuggestionSaga> next)
    {
        await PrepareCoreAsync(context);
        await next.Execute(context).ConfigureAwait(false);
    }

    public async Task Execute<T>(BehaviorContext<TicketSuggestionSaga, T> context, IBehavior<TicketSuggestionSaga, T> next)
        where T : class
    {
        await PrepareCoreAsync(context);
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

    private async Task PrepareCoreAsync(BehaviorContext<TicketSuggestionSaga> context)
    {
        var saga = context.Saga;
        if (saga.PendingReconcileAction != ReconcileActions.Retry)
            return;

        var attemptId = saga.CurrentAttemptId;
        if (attemptId != Guid.Empty)
        {
            var attempt = await attemptReader.GetByAttemptIdAsync(attemptId, context.CancellationToken);
            if (attempt is not null && AiGenerationAttemptStatuses.IsActive(attempt.Status))
            {
                var supersede = await attemptLifecycle.TrySupersedeAsync(
                    attemptId,
                    "Superseded before saga generation retry.",
                    context.CancellationToken);

                if (supersede is SupersedeAttemptOutcome.ConcurrencyConflict)
                {
                    logger.LogInformation(
                        "Prepare retry deferred — could not supersede active attempt SagaId={SagaId} AttemptId={AttemptId}",
                        saga.CorrelationId,
                        attemptId);
                    saga.PendingReconcileAction = ReconcileActions.WaitForGeneration;
                    saga.UpdatedAt = DateTimeOffset.UtcNow;
                    return;
                }
            }
        }

        saga.RetryCount++;
        TicketSuggestionActivities.StartNewAttempt(saga);
        saga.PendingReconcileAction = ReconcileActions.Retry;
        saga.UpdatedAt = DateTimeOffset.UtcNow;

        logger.LogInformation(
            "Prepare retry ready SagaId={SagaId} NewAttemptId={AttemptId} RetryCount={RetryCount}",
            saga.CorrelationId,
            saga.CurrentAttemptId,
            saga.RetryCount);
    }
}
