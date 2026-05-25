namespace SupportPoc.AiOrchestrator.Data;

public sealed class SagaLogEntryEntity
{
    public required string Id { get; set; }
    public required string EventId { get; set; }
    public required string TicketId { get; set; }
    public required string Step { get; set; }
    public required string Status { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
