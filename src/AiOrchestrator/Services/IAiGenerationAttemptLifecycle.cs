namespace SupportPoc.AiOrchestrator.Services;

public enum SupersedeAttemptOutcome
{
    Applied,
    NotFound,
    AlreadyInactive,
    ConcurrencyConflict
}

public interface IAiGenerationAttemptLifecycle
{
    Task<bool> HasActiveAttemptForJobAsync(
        Guid jobId,
        Guid? excludingAttemptId = null,
        CancellationToken cancellationToken = default);

    Task<SupersedeAttemptOutcome> TrySupersedeAsync(
        Guid attemptId,
        string reason,
        CancellationToken cancellationToken = default);
}
