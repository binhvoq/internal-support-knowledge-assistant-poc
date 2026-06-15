using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class AiGenerationAttemptLifecycle(
    OrchestratorDbContext db,
    ILogger<AiGenerationAttemptLifecycle> logger) : IAiGenerationAttemptLifecycle
{
    public async Task<bool> HasActiveAttemptForJobAsync(
        Guid jobId,
        Guid? excludingAttemptId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.AiGenerationAttempts
            .AsNoTracking()
            .Where(x => x.JobId == jobId
                && (x.Status == AiGenerationAttemptStatus.Pending
                    || x.Status == AiGenerationAttemptStatus.Running));

        if (excludingAttemptId is not null)
            query = query.Where(x => x.AttemptId != excludingAttemptId.Value);

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<SupersedeAttemptOutcome> TrySupersedeAsync(
        Guid attemptId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await db.AiGenerationAttempts
            .Where(x => x.AttemptId == attemptId
                && (x.Status == AiGenerationAttemptStatus.Pending
                    || x.Status == AiGenerationAttemptStatus.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, AiGenerationAttemptStatus.Superseded)
                .SetProperty(x => x.Error, reason)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        if (affected == 1)
        {
            logger.LogInformation(
                "AI attempt superseded AttemptId={AttemptId} Reason={Reason}",
                attemptId,
                reason);
            return SupersedeAttemptOutcome.Applied;
        }

        var row = await db.AiGenerationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AttemptId == attemptId, cancellationToken);

        if (row is null)
            return SupersedeAttemptOutcome.NotFound;

        if (!AiGenerationAttemptStatuses.IsActive(row.Status))
            return SupersedeAttemptOutcome.AlreadyInactive;

        logger.LogInformation(
            "AI attempt supersede concurrency conflict AttemptId={AttemptId} Status={Status}",
            attemptId,
            row.Status);
        return SupersedeAttemptOutcome.ConcurrencyConflict;
    }
}
