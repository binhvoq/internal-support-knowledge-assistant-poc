namespace SupportPoc.AiOrchestrator.Services;

public interface IAiGenerationAttemptReader
{
    Task<AiGenerationAttemptSnapshot?> GetByAttemptIdAsync(Guid attemptId, CancellationToken cancellationToken = default);
}
