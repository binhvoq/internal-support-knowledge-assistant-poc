using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Services;

public interface ISagaReconciliationQueue
{
    Task UpsertOnEscalateAsync(TicketSuggestionSaga saga, string reason, CancellationToken ct = default);
    /// <summary>Called by sweeper when scheduling auto-redrive — increments auto attempt count immediately to prevent duplicate enqueue.</summary>
    Task RecordScheduledAutoRedriveAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default);
    Task MarkResolvedAsync(Guid sagaId, string resolution, DateTimeOffset now, CancellationToken ct = default);
    Task<int> BackfillMissingItemsAsync(IEnumerable<TicketSuggestionSaga> unknownSagas, CancellationToken ct = default);
    Task<SagaReconciliationItem?> GetAsync(Guid sagaId, CancellationToken ct = default);
}

internal sealed class SagaReconciliationQueue(OrchestratorDbContext db) : ISagaReconciliationQueue
{
    internal const string BackfillReason =
        "Backfilled missing reconciliation item for parked ReconcileUnknown saga.";

    public async Task UpsertOnEscalateAsync(TicketSuggestionSaga saga, string reason, CancellationToken ct = default)
    {
        var existing = await db.SagaReconciliationItems
            .FirstOrDefaultAsync(x => x.SagaId == saga.CorrelationId, ct);
        if (existing is not null)
        {
            existing.Reason = reason;
            existing.LastAttemptAt = saga.LastReconcileAttemptAt;
            await db.SaveChangesAsync(ct);
            return;
        }

        db.SagaReconciliationItems.Add(new SagaReconciliationItem
        {
            SagaId = saga.CorrelationId,
            TicketId = saga.TicketId,
            JobId = saga.JobId,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAttemptAt = saga.LastReconcileAttemptAt,
            AttemptCount = 0
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordScheduledAutoRedriveAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default)
    {
        var item = await db.SagaReconciliationItems
            .FirstOrDefaultAsync(x => x.SagaId == sagaId, ct);
        if (item is null || item.ResolvedAt is not null)
            return;

        item.AttemptCount++;
        item.LastAttemptAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkResolvedAsync(Guid sagaId, string resolution, DateTimeOffset now, CancellationToken ct = default)
    {
        var item = await db.SagaReconciliationItems
            .FirstOrDefaultAsync(x => x.SagaId == sagaId, ct);
        if (item is null || item.ResolvedAt is not null)
            return;

        item.Resolution = resolution;
        item.ResolvedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> BackfillMissingItemsAsync(IEnumerable<TicketSuggestionSaga> unknownSagas, CancellationToken ct = default)
    {
        var created = 0;
        foreach (var saga in unknownSagas)
        {
            var exists = await db.SagaReconciliationItems
                .AnyAsync(x => x.SagaId == saga.CorrelationId, ct);
            if (exists)
                continue;

            db.SagaReconciliationItems.Add(new SagaReconciliationItem
            {
                SagaId = saga.CorrelationId,
                TicketId = saga.TicketId,
                JobId = saga.JobId,
                Reason = saga.FailureReason ?? BackfillReason,
                CreatedAt = saga.UpdatedAt,
                LastAttemptAt = saga.LastReconcileAttemptAt,
                AttemptCount = 0
            });
            created++;
        }

        if (created > 0)
            await db.SaveChangesAsync(ct);

        return created;
    }

    public Task<SagaReconciliationItem?> GetAsync(Guid sagaId, CancellationToken ct = default) =>
        db.SagaReconciliationItems.FirstOrDefaultAsync(x => x.SagaId == sagaId, ct);
}
