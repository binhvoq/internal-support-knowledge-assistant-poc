using MassTransit;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Activities;

public sealed class EvaluateSavingTimeoutActivity :
    IStateMachineActivity<TicketSuggestionState, ISagaTimeoutExpired>,
    IStateMachineActivity<TicketSuggestionState, ISagaVerifyDue>
{
    private readonly ISavingTimeoutEvaluator _evaluator;
    private readonly ILogger<EvaluateSavingTimeoutActivity> _logger;

    public EvaluateSavingTimeoutActivity(
        ISavingTimeoutEvaluator evaluator,
        ILogger<EvaluateSavingTimeoutActivity> logger)
    {
        _evaluator = evaluator;
        _logger = logger;
    }

    public void Probe(ProbeContext context) => context.CreateScope("evaluateSavingTimeout");

    public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);

    public Task Execute(
        BehaviorContext<TicketSuggestionState, ISagaTimeoutExpired> context,
        IBehavior<TicketSuggestionState, ISagaTimeoutExpired> next) =>
        RunAsync(context, next);

    public Task Execute(
        BehaviorContext<TicketSuggestionState, ISagaVerifyDue> context,
        IBehavior<TicketSuggestionState, ISagaVerifyDue> next) =>
        RunAsync(context, next);

    public Task Faulted<TException>(
        BehaviorExceptionContext<TicketSuggestionState, ISagaTimeoutExpired, TException> context,
        IBehavior<TicketSuggestionState, ISagaTimeoutExpired> next)
        where TException : Exception =>
        next.Faulted(context);

    public Task Faulted<TException>(
        BehaviorExceptionContext<TicketSuggestionState, ISagaVerifyDue, TException> context,
        IBehavior<TicketSuggestionState, ISagaVerifyDue> next)
        where TException : Exception =>
        next.Faulted(context);

    private async Task RunAsync<T>(BehaviorContext<TicketSuggestionState, T> context, IBehavior<TicketSuggestionState, T> next)
        where T : class
    {
        var saga = context.Saga;

        try
        {
            var timeoutContext = new SagaTimeoutContext(
                saga,
                saga.TimeoutVerifyAttempts,
                saga.SaveResendIssued,
                saga.PostResendVerifyAttempts);

            var decision = await _evaluator.EvaluateAsync(timeoutContext, context.CancellationToken);

            saga.PendingTimeoutOutcome = decision.Outcome.ToString();
            saga.TimeoutDecisionReason = decision.Reason;
            saga.TimeoutVerifyAttempts++;

            if (saga.SaveResendIssued)
                saga.PostResendVerifyAttempts++;

            saga.UpdatedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Saving timeout evaluated SagaId={SagaId} Outcome={Outcome} Reason={Reason} VerifyAttempts={Attempts} PostResend={PostResend}",
                saga.CorrelationId,
                decision.Outcome,
                decision.Reason,
                saga.TimeoutVerifyAttempts,
                saga.PostResendVerifyAttempts);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving timeout evaluate failed SagaId={SagaId}", saga.CorrelationId);
            saga.PendingTimeoutOutcome = SagaTimeoutOutcome.Fail.ToString();
            saga.TimeoutDecisionReason = $"Evaluate error: {ex.Message}";
            saga.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await next.Execute(context);
    }
}
