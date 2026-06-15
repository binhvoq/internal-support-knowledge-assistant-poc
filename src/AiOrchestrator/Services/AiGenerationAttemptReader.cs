using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class AiGenerationAttemptReader(OrchestratorDbContext db) : IAiGenerationAttemptReader
{
    public async Task<AiGenerationAttemptSnapshot?> GetByAttemptIdAsync(
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        var row = await db.AiGenerationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AttemptId == attemptId, cancellationToken);

        return row is null
            ? null
            : new AiGenerationAttemptSnapshot(
                row.AttemptId,
                row.Status,
                row.LeaseUntil,
                row.StartedAt,
                row.Category,
                row.Suggestion,
                row.RelatedDocumentsJson,
                row.Error);
    }
}
