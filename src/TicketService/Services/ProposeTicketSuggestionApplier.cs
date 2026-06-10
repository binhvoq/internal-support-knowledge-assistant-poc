using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

public sealed class ProposeTicketSuggestionApplier(TicketDbContext db, ILogger<ProposeTicketSuggestionApplier> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record Outcome(bool Accepted, string? RejectReason);

    public async Task<Outcome> ApplyAsync(IProposeTicketSuggestion msg, CancellationToken cancellationToken = default)
    {
        var existing = await db.ProcessedCommands.FindAsync([msg.CommandId], cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "Propose idempotent replay CommandId={CommandId} TicketId={TicketId} Accepted={Accepted}",
                msg.CommandId,
                msg.TicketId,
                existing.Accepted);
            return new Outcome(existing.Accepted, existing.RejectReason);
        }

        var ticket = await db.Tickets.FindAsync([msg.TicketId], cancellationToken);
        if (ticket is null)
        {
            logger.LogWarning("Propose: ticket not found TicketId={TicketId}", msg.TicketId);
            return await PersistOutcomeOnlyAsync(msg, false, "Ticket not found", cancellationToken);
        }

        var rejectReason = AutoSuggestionRules.GetRejectReason(ticket, msg.ExpectedTicketVersion);
        if (rejectReason is not null)
        {
            if (IsAlreadyAcceptedBySameJob(rejectReason)
                && await WasAcceptedBySameJobAsync(msg.TicketId, msg.JobId, cancellationToken))
            {
                logger.LogInformation(
                    "Propose idempotent accept same job TicketId={TicketId} JobId={JobId}",
                    msg.TicketId,
                    msg.JobId);
                return await PersistOutcomeOnlyAsync(msg, true, null, cancellationToken);
            }

            logger.LogInformation(
                "Propose rejected TicketId={TicketId} JobId={JobId} Reason={Reason}",
                msg.TicketId,
                msg.JobId,
                rejectReason);
            return await PersistOutcomeOnlyAsync(msg, false, rejectReason, cancellationToken);
        }

        ticket.Status = TicketStatus.Suggested;
        ticket.Category = msg.Category;
        ticket.AiSuggestedAnswer = msg.Suggestion;
        ticket.RelatedDocumentsJson = JsonSerializer.Serialize(msg.RelatedDocuments, JsonOptions);
        ticket.Version++;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        StageOutcome(msg, true, null);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Propose accepted TicketId={TicketId} JobId={JobId}", msg.TicketId, msg.JobId);
        return new Outcome(true, null);
    }

    private static bool IsAlreadyAcceptedBySameJob(string rejectReason) =>
        rejectReason.Contains("already has an accepted AI suggestion", StringComparison.OrdinalIgnoreCase);

    private Task<bool> WasAcceptedBySameJobAsync(string ticketId, Guid jobId, CancellationToken cancellationToken) =>
        db.ProcessedCommands.AnyAsync(
            c => c.TicketId == ticketId && c.JobId == jobId && c.Accepted,
            cancellationToken);

    private void StageOutcome(IProposeTicketSuggestion msg, bool accepted, string? rejectReason)
    {
        db.ProcessedCommands.Add(new ProcessedCommandEntity
        {
            CommandId = msg.CommandId,
            TicketId = msg.TicketId,
            JobId = msg.JobId,
            Accepted = accepted,
            RejectReason = rejectReason,
            ProcessedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task<Outcome> PersistOutcomeOnlyAsync(
        IProposeTicketSuggestion msg,
        bool accepted,
        string? rejectReason,
        CancellationToken cancellationToken)
    {
        StageOutcome(msg, accepted, rejectReason);
        await db.SaveChangesAsync(cancellationToken);
        return new Outcome(accepted, rejectReason);
    }
}
