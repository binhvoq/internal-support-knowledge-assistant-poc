using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Policies;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts;

public interface ICompensatingTimeoutEvaluator
{
    Task<SagaTimeoutDecision> EvaluateAsync(StepTimeoutContext context, CancellationToken cancellationToken);
}

public sealed class CompensatingTimeoutEvaluator(
    ITicketProgressProbe probe,
    CompensatingTimeoutPolicy policy) : ICompensatingTimeoutEvaluator
{
    public async Task<SagaTimeoutDecision> EvaluateAsync(StepTimeoutContext context, CancellationToken cancellationToken)
    {
        var probeResult = await probe.GetAsync(context.Saga.TicketId, cancellationToken);
        return policy.Decide(probeResult, context);
    }
}
