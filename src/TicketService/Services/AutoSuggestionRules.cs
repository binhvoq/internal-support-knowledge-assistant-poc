using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

/// <summary>Ticket aggregate quyet nhan hay bo proposal — khong dung epoch/saga metadata.</summary>
public static class AutoSuggestionRules
{
    public static bool CanAccept(TicketEntity ticket) => GetRejectReason(ticket) is null;

    public static string? GetRejectReason(TicketEntity ticket)
    {
        if (!string.IsNullOrWhiteSpace(ticket.FinalAnswer))
            return "Ticket already has a final answer.";

        if (ticket.Status is TicketStatus.Resolved or TicketStatus.Reopened)
            return $"Ticket status is {ticket.Status}.";

        if (!string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
            return "Ticket already has an accepted AI suggestion.";

        if (ticket.Status != TicketStatus.New)
            return $"Ticket status is {ticket.Status}; only New tickets accept auto suggestion.";

        return null;
    }

    /// <summary>Idempotent replay: da apply suggestion cho cung job (caller truyen jobId neu can mo rong).</summary>
    public static bool IsAlreadyAccepted(TicketEntity ticket) =>
        ticket.Status == TicketStatus.Suggested && !string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer);
}
