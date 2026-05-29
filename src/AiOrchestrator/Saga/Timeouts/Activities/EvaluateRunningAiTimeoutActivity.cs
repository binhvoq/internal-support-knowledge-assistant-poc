using MassTransit;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Activities;

public sealed class EvaluateRunningAiTimeoutActivity :
    IStateMachineActivity<TicketSuggestionState, ISagaTimeoutExpired>,
    IStateMachineActivity<TicketSuggestionState, ISagaVerifyDue>
{
    private readonly IRunningAiTimeoutEvaluator _evaluator;
    private readonly SagaTimeoutOptions _options;
    private readonly ILogger<EvaluateRunningAiTimeoutActivity> _logger;

    public EvaluateRunningAiTimeoutActivity(
        IRunningAiTimeoutEvaluator evaluator,
        IOptions<SagaTimeoutOptions> options,
        ILogger<EvaluateRunningAiTimeoutActivity> logger)
    {
        _evaluator = evaluator;
        _options = options.Value;
        _logger = logger;
    }

    public void Probe(ProbeContext context) => context.CreateScope("evaluateRunningAiTimeout");

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
            var timeoutContext = new StepTimeoutContext(
                saga,
                _options.RunningAi,
                saga.TimeoutVerifyAttempts,
                saga.PostResendVerifyAttempts,
                saga.AiRunResendCount);

            var decision = await _evaluator.EvaluateAsync(timeoutContext, context.CancellationToken);

            saga.PendingTimeoutOutcome = decision.Outcome.ToString();
            saga.TimeoutDecisionReason = decision.Reason;
            saga.TimeoutVerifyAttempts++;

            if (saga.AiRunResendCount > 0)
                saga.PostResendVerifyAttempts++;

            saga.UpdatedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "RunningAi timeout evaluated SagaId={SagaId} Outcome={Outcome} Reason={Reason} VerifyAttempts={Attempts} AiResends={Resends} PostResend={PostResend}",
                saga.CorrelationId,
                decision.Outcome,
                decision.Reason,
                saga.TimeoutVerifyAttempts,
                saga.AiRunResendCount,
                saga.PostResendVerifyAttempts);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunningAi timeout evaluate failed SagaId={SagaId}", saga.CorrelationId);
            saga.PendingTimeoutOutcome = SagaTimeoutOutcome.Fail.ToString();
            saga.TimeoutDecisionReason = $"Evaluate error: {ex.Message}";
            saga.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await next.Execute(context);
    }
}
