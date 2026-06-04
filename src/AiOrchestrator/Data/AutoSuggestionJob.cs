namespace SupportPoc.AiOrchestrator.Data;

/// <summary>Proposal pipeline state — khong mirror ownership/epoch len ticket.</summary>
public sealed class AutoSuggestionJob
{
    public Guid JobId { get; set; }
    public required string TicketId { get; set; }
    public required string EmployeeId { get; set; }
    public required string Question { get; set; }
    public required string Category { get; set; }
    public required string Status { get; set; }
    public string? ProducedCategory { get; set; }
    public string? ProducedSuggestion { get; set; }
    public string ProducedRelatedDocumentsJson { get; set; } = "[]";
    public string? FailureReason { get; set; }
    public string? DiscardReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
