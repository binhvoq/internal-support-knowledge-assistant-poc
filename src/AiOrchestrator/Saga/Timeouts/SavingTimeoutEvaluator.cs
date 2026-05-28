using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Policies;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts;

public sealed class SavingTimeoutEvaluator(
    ITicketProgressProbe probe,
    SavingTimeoutPolicy policy) : ISavingTimeoutEvaluator
{
    public async Task<SagaTimeoutDecision> EvaluateAsync(SagaTimeoutContext context, CancellationToken cancellationToken)
    {
        var probeResult = await probe.GetAsync(context.Saga.TicketId, cancellationToken);
        return policy.Decide(probeResult, context);
    }
}
