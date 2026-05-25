namespace SupportPoc.AiOrchestrator.Data;

public sealed class InboxMessageEntity
{
    public required string EventId { get; set; }
    public required string Consumer { get; set; }
    public required string Status { get; set; }
    public string? TicketId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
