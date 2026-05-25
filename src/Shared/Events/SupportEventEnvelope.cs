namespace SupportPoc.Shared.Events;

public sealed class SupportEventEnvelope
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required object Payload { get; init; }
}

public sealed class TicketCreatedPayload
{
    public required string TicketId { get; init; }
}

public sealed class TicketResolvedPayload
{
    public required string TicketId { get; init; }
}

public sealed class AiSuggestionGeneratedPayload
{
    public required string TicketId { get; init; }
}
