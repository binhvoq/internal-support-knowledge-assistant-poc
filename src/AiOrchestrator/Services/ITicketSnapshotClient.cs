namespace SupportPoc.AiOrchestrator.Services;

public sealed record TicketSnapshot(
    string TicketId,
    string Status,
    long Version,
    bool HasAiSuggestion,
    bool HasFinalAnswer);

public interface ITicketSnapshotClient
{
    Task<TicketSnapshot?> GetTicketAsync(string ticketId, CancellationToken cancellationToken = default);
}
