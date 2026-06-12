using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

public interface ITicketSuggestionReconcileClient
{
    Task<AutoSuggestionReconcileResult> ReconcileAsync(
        string ticketId,
        Guid jobId,
        long? expectedVersion,
        CancellationToken cancellationToken = default);
}
