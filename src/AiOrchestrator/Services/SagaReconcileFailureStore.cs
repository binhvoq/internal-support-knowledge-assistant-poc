using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Services;

public interface ISagaReconcileFailureStore
{
    Task RecordTransientFailureAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default);
    Task RecordSuccessAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default);
    Task RecordScheduledAttemptAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default);
}

internal sealed class SagaReconcileFailureStore(
    IServiceProvider serviceProvider,
    ILogger<SagaReconcileFailureStore> logger) : ISagaReconcileFailureStore
{
    public async Task RecordTransientFailureAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var saga = await db.TicketSuggestionSagas
                .FirstOrDefaultAsync(s => s.CorrelationId == sagaId, ct);
            if (saga is null)
            {
                logger.LogWarning("Saga not found when recording transient failure. SagaId={SagaId}", sagaId);
                return;
            }

            ReconcileTransientTracker.RecordTransientFailure(saga, now);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist reconcile transient failure counter. SagaId={SagaId}", sagaId);
            // Do not throw: this is side persistence to survive saga tx rollback.
        }
    }

    public async Task RecordSuccessAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var saga = await db.TicketSuggestionSagas
                .FirstOrDefaultAsync(s => s.CorrelationId == sagaId, ct);
            if (saga is null)
            {
                logger.LogWarning("Saga not found when recording reconcile success. SagaId={SagaId}", sagaId);
                return;
            }

            ReconcileTransientTracker.RecordSuccess(saga, now);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist reconcile success (counter reset). SagaId={SagaId}", sagaId);
        }
    }

    public async Task RecordScheduledAttemptAsync(Guid sagaId, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var saga = await db.TicketSuggestionSagas
                .FirstOrDefaultAsync(s => s.CorrelationId == sagaId, ct);
            if (saga is null)
            {
                logger.LogWarning("Saga not found when recording scheduled reconcile attempt. SagaId={SagaId}", sagaId);
                return;
            }

            ReconcileTransientTracker.RecordScheduledAttempt(saga, now);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist scheduled reconcile attempt (LastReconcileAttemptAt). SagaId={SagaId}", sagaId);
        }
    }
}
