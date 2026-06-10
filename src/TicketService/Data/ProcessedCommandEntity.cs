namespace SupportPoc.TicketService.Data;

/// <summary>Idempotency store cho ProposeTicketSuggestion theo CommandId.</summary>
public sealed class ProcessedCommandEntity
{
    public Guid CommandId { get; set; }
    public required string TicketId { get; set; }
    public Guid JobId { get; set; }
    public bool Accepted { get; set; }
    public string? RejectReason { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
