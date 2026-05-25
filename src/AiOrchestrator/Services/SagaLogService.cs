using SupportPoc.AiOrchestrator.Data;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class SagaLogService
{
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<SagaLogService> _logger;

    public SagaLogService(OrchestratorDbContext db, ILogger<SagaLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task AddAsync(string eventId, string ticketId, string step, string status, string? detail, CancellationToken cancellationToken)
    {
        _db.SagaLogEntries.Add(new SagaLogEntryEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            EventId = eventId,
            TicketId = ticketId,
            Step = step,
            Status = status,
            Detail = detail,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saga {Step} {Status} EventId={EventId} TicketId={TicketId} {Detail}", step, status, eventId, ticketId, detail);
    }
}
