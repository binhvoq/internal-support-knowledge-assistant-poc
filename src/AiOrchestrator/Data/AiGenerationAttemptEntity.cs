namespace SupportPoc.AiOrchestrator.Data;

public static class AiGenerationAttemptStatus
{
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
    public string Status { get; set; } = AiGenerationAttemptStatus.Running;
    public string? Category { get; set; }
    public string? Suggestion { get; set; }
    public string RelatedDocumentsJson { get; set; } = "[]";
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
