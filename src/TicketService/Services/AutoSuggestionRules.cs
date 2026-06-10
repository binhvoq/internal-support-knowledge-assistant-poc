using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

/// <summary>Ticket aggregate quyet nhan hay bo proposal — saga khong duoc ep lifecycle.</summary>
public static class AutoSuggestionRules
{
    public static bool CanAccept(TicketEntity ticket, long? expectedVersion = null) =>
        GetRejectReason(ticket, expectedVersion) is null;

    public static string? GetRejectReason(TicketEntity ticket, long? expectedVersion = null)
    {
        if (!string.IsNullOrWhiteSpace(ticket.FinalAnswer))
            return "Ticket already has a final answer.";

        if (ticket.Status is TicketStatus.Resolved or TicketStatus.Reopened)
            return $"Ticket status is {ticket.Status}.";

        if (!string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
            return "Ticket already has an accepted AI suggestion.";

        if (ticket.Status != TicketStatus.New)
            return $"Ticket status is {ticket.Status}; only New tickets accept auto suggestion.";

        if (expectedVersion is not null && ticket.Version != expectedVersion)
            return "Ticket version mismatch (stale command).";

        return null;
    }
}
