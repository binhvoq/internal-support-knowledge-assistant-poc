namespace SupportPoc.Shared.Models;

public sealed record AutoSuggestionReconcileResult(
    string TicketId,
    Guid JobId,
    string Decision,
    string? Reason,
    string? CurrentTicketStatus,
    long? CurrentVersion,
    bool HasAiSuggestion,
    bool HasFinalAnswer);
