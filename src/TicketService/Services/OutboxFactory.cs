using System.Text.Json;
using SupportPoc.Shared.Events;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

internal static class OutboxFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static OutboxMessageEntity Create<TPayload>(string eventType, TPayload payload)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new SupportEventEnvelope
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            OccurredAt = now,
            Payload = payload!
        };

        return new OutboxMessageEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            EventId = envelope.EventId,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
            Status = "Pending",
            CreatedAt = now
        };
    }
}
