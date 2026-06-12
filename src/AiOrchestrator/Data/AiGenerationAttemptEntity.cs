namespace SupportPoc.AiOrchestrator.Data;

public static class AiGenerationAttemptStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public sealed class AiGenerationAttemptEntity
{
    public Guid AttemptId { get; set; }
    public Guid SagaId { get; set; }
    public Guid JobId { get; set; }
    public string TicketId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string RequestedCategory { get; set; } = string.Empty;
    public string Status { get; set; } = AiGenerationAttemptStatus.Pending;
    public string? Category { get; set; }
    public string? Suggestion { get; set; }
    public string RelatedDocumentsJson { get; set; } = "[]";
    public string? Error { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [0, 0, 0, 0, 0, 0, 0, 1];
}
