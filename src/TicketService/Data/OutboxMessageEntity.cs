namespace SupportPoc.TicketService.Data;

public sealed class OutboxMessageEntity
{
    public required string Id { get; set; }
    public required string EventId { get; set; }
    public required string EventType { get; set; }
    public required string PayloadJson { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}
