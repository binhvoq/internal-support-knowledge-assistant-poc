using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class InboxService
{
    private const string ConsumerName = "ai-orchestrator";
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<InboxService> _logger;

    public InboxService(OrchestratorDbContext db, ILogger<InboxService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> TryStartAsync(string eventId, string ticketId, CancellationToken cancellationToken)
    {
        var existing = await _db.InboxMessages.FindAsync([ConsumerName, eventId], cancellationToken);
        if (existing is { Status: "Processed" })
        {
            _logger.LogInformation("Inbox skip processed EventId={EventId} TicketId={TicketId}", eventId, ticketId);
            return false;
        }

        if (existing is null)
        {
            _db.InboxMessages.Add(new InboxMessageEntity
            {
                Consumer = ConsumerName,
                EventId = eventId,
                TicketId = ticketId,
                Status = "Processing",
                ReceivedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Status = "Processing";
            existing.TicketId = ticketId;
            existing.Error = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Inbox processing EventId={EventId} TicketId={TicketId}", eventId, ticketId);
        return true;
    }

    public async Task MarkProcessedAsync(string eventId, CancellationToken cancellationToken)
    {
        var existing = await _db.InboxMessages.FindAsync([ConsumerName, eventId], cancellationToken);
        if (existing is null) return;
        existing.Status = "Processed";
        existing.ProcessedAt = DateTimeOffset.UtcNow;
        existing.Error = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(string eventId, Exception ex, CancellationToken cancellationToken)
    {
        var existing = await _db.InboxMessages.FindAsync([ConsumerName, eventId], cancellationToken);
        if (existing is null) return;
        existing.Status = "Failed";
        existing.Error = ex.Message;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
