using MassTransit;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Activities;

public sealed class EvaluateCompensatingTimeoutActivity :
    IStateMachineActivity<TicketSuggestionState, ISagaTimeoutExpired>,
    IStateMachineActivity<TicketSuggestionState, ISagaVerifyDue>
{
    private readonly ICompensatingTimeoutEvaluator _evaluator;
    private readonly SagaTimeoutOptions _options;
    private readonly ILogger<EvaluateCompensatingTimeoutActivity> _logger;

    public EvaluateCompensatingTimeoutActivity(
        ICompensatingTimeoutEvaluator evaluator,
        IOptions<SagaTimeoutOptions> options,
        ILogger<EvaluateCompensatingTimeoutActivity> logger)
    {
        _evaluator = evaluator;
        _options = options.Value;
        _logger = logger;
    }

    public void Probe(ProbeContext context) => context.CreateScope("evaluateCompensatingTimeout");

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
                _options.Compensating,
                saga.TimeoutVerifyAttempts,
                saga.PostResendVerifyAttempts,
                saga.CompensateResendCount);

            var decision = await _evaluator.EvaluateAsync(timeoutContext, context.CancellationToken);

            saga.PendingTimeoutOutcome = decision.Outcome.ToString();
            saga.TimeoutDecisionReason = decision.Reason;
            saga.TimeoutVerifyAttempts++;

            if (saga.CompensateResendCount > 0)
                saga.PostResendVerifyAttempts++;

            saga.UpdatedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Compensating timeout evaluated SagaId={SagaId} Outcome={Outcome} Reason={Reason} VerifyAttempts={Attempts} CompensateResends={Resends} PostResend={PostResend}",
                saga.CorrelationId,
                decision.Outcome,
                decision.Reason,
                saga.TimeoutVerifyAttempts,
                saga.CompensateResendCount,
                saga.PostResendVerifyAttempts);

            if (decision.Outcome == SagaTimeoutOutcome.Fail)
            {
                var probeError = IsProbeVerificationFailure(decision.Reason)
                    ? decision.Reason
                    : null;
                SagaCompensationDiagnostics.LogCompensationFailed(_logger, saga, decision.Reason, probeError);
            }
            else if (decision.Outcome == SagaTimeoutOutcome.RetryVerify &&
                     IsProbeVerificationFailure(decision.Reason))
            {
                SagaCompensationDiagnostics.LogProbeUnavailable(
                    _logger,
                    saga,
                    decision.Reason,
                    saga.TimeoutVerifyAttempts,
                    _options.Compensating.MaxVerifyAttempts);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compensating timeout evaluate failed SagaId={SagaId}", saga.CorrelationId);
            saga.PendingTimeoutOutcome = SagaTimeoutOutcome.Fail.ToString();
            saga.TimeoutDecisionReason = $"Evaluate error: {ex.Message}";
            saga.UpdatedAt = DateTimeOffset.UtcNow;
            SagaCompensationDiagnostics.LogCompensationFailed(_logger, saga, saga.TimeoutDecisionReason, ex.Message);
        }

        await next.Execute(context);
    }

    private static bool IsProbeVerificationFailure(string reason) =>
        reason.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("unable to verify", StringComparison.OrdinalIgnoreCase);
}
