using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

public sealed class ConsiderAutoSuggestionApplier(TicketDbContext db, ILogger<ConsiderAutoSuggestionApplier> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record Outcome(bool Accepted, string? RejectReason);

    public async Task<Outcome> ApplyAsync(IConsiderAutoSuggestion msg, CancellationToken cancellationToken = default)
    {
        var ticket = await db.Tickets.FindAsync([msg.TicketId], cancellationToken);
        if (ticket is null)
        {
            logger.LogWarning("Consider: ticket not found TicketId={TicketId}", msg.TicketId);
            return new Outcome(false, "Ticket not found");
        }

        if (AutoSuggestionRules.IsAlreadyAccepted(ticket))
        {
            logger.LogInformation(
                "Consider idempotent accept TicketId={TicketId} JobId={JobId}",
                msg.TicketId,
                msg.JobId);
            return new Outcome(true, null);
        }

        var rejectReason = AutoSuggestionRules.GetRejectReason(ticket);
        if (rejectReason is not null)
        {
            logger.LogInformation(
                "Consider rejected TicketId={TicketId} JobId={JobId} Reason={Reason}",
                msg.TicketId,
                msg.JobId,
                rejectReason);
            return new Outcome(false, rejectReason);
        }

        ticket.Status = TicketStatus.Suggested;
        ticket.Category = msg.Category;
        ticket.AiSuggestedAnswer = msg.Suggestion;
        ticket.RelatedDocumentsJson = JsonSerializer.Serialize(msg.RelatedDocuments, JsonOptions);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Consider accepted TicketId={TicketId} JobId={JobId}", msg.TicketId, msg.JobId);
        return new Outcome(true, null);
    }
}
