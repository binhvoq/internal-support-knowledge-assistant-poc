using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

/// <summary>
/// Mutate ticket lifecycle status (agent action). Applies saga ownership override when automation is active.
/// PoC: any valid TicketStatus is allowed — no transition matrix (production should add one).
/// </summary>
public static class TicketLifecycleMutation
{
    public static bool TryMutateStatus(
        TicketEntity ticket,
        string requestedStatus,
        string? finalAnswer,
        out string? error)
    {
        var status = NormalizeStatus(requestedStatus);
        if (status is null)
        {
            error = $"status khong hop le. Cho phep: {string.Join(", ", TicketStatus.All)}.";
            return false;
        }

        if (status == TicketStatus.Resolved && string.IsNullOrWhiteSpace(finalAnswer))
        {
            error = "finalAnswer la bat buoc khi chuyen sang Resolved.";
            return false;
        }

        TicketSagaOwnership.ApplyAgentLifecycleOverride(ticket);

        ticket.Status = status;
        if (status == TicketStatus.Resolved)
            ticket.FinalAnswer = finalAnswer!.Trim();
        else if (finalAnswer is not null)
            ticket.FinalAnswer = string.IsNullOrWhiteSpace(finalAnswer) ? null : finalAnswer.Trim();
        else if (status == TicketStatus.Reopened)
            ticket.FinalAnswer = null;

        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        error = null;
        return true;
    }

    private static string? NormalizeStatus(string raw)
    {
        var trimmed = raw.Trim();
        foreach (var allowed in TicketStatus.All)
        {
            if (string.Equals(trimmed, allowed, StringComparison.OrdinalIgnoreCase))
                return allowed;
        }

        return null;
    }
}
