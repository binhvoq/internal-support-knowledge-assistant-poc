using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts;

public interface ISavingTimeoutEvaluator
{
    Task<SagaTimeoutDecision> EvaluateAsync(StepTimeoutContext context, CancellationToken cancellationToken);
}
