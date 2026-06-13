namespace SupportPoc.AiOrchestrator.Data;

/// <summary>Lightweight queue tracking sagas parked in ReconcileUnknown for ops visibility and redrive limits.</summary>
public sealed class SagaReconciliationItem
{
    public Guid SagaId { get; set; }
    public string TicketId { get; set; } = string.Empty;
    public Guid JobId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
}
