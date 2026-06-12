using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        var docsJson = JsonSerializer.Serialize(msg.RelatedDocuments, JsonOptions);
        var now = DateTimeOffset.UtcNow;

        var ownsTransaction = db.Database.CurrentTransaction is null;
        IDbContextTransaction? ownedTransaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
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

            var candidates = db.Tickets.Where(t =>
                t.Id == msg.TicketId &&
                t.Status == TicketStatus.New &&
                t.AiSuggestedAnswer == null &&
                t.FinalAnswer == null);

            if (msg.ExpectedTicketVersion is not null)
                candidates = candidates.Where(t => t.Version == msg.ExpectedTicketVersion.Value);

            var affected = await candidates.ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, TicketStatus.Suggested)
                .SetProperty(t => t.Category, msg.Category)
                .SetProperty(t => t.AiSuggestedAnswer, msg.Suggestion)
                .SetProperty(t => t.RelatedDocumentsJson, docsJson)
                .SetProperty(t => t.Version, t => t.Version + 1)
                .SetProperty(t => t.UpdatedAt, now),
                cancellationToken);

            if (affected == 1)
            {
                logger.LogInformation("Propose accepted TicketId={TicketId} JobId={JobId}", msg.TicketId, msg.JobId);
                return await CommitOutcomeAsync(msg, true, null, ownedTransaction, cancellationToken);
            }

            var ticket = await db.Tickets.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == msg.TicketId, cancellationToken);

            if (ticket is null)
            {
                logger.LogWarning("Propose: ticket not found TicketId={TicketId}", msg.TicketId);
                return await CommitOutcomeAsync(msg, false, "Ticket not found", ownedTransaction, cancellationToken);
            }

            var rejectReason = AutoSuggestionRules.GetRejectReason(ticket, msg.ExpectedTicketVersion);
            if (rejectReason is not null
                && IsAlreadyAcceptedBySameJob(rejectReason)
                && await WasAcceptedBySameJobAsync(msg.TicketId, msg.JobId, cancellationToken))
            {
                logger.LogInformation(
                    "Propose idempotent accept same job TicketId={TicketId} JobId={JobId}",
                    msg.TicketId,
                    msg.JobId);
                return await CommitOutcomeAsync(msg, true, null, ownedTransaction, cancellationToken);
            }

            rejectReason ??= "Ticket changed while applying suggestion.";

            logger.LogInformation(
                "Propose rejected TicketId={TicketId} JobId={JobId} Reason={Reason}",
                msg.TicketId,
                msg.JobId,
                rejectReason);
            return await CommitOutcomeAsync(msg, false, rejectReason, ownedTransaction, cancellationToken);
        }
        finally
        {
            if (ownedTransaction is not null)
                await ownedTransaction.DisposeAsync();
        }
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

    private async Task<Outcome> CommitOutcomeAsync(
        IProposeTicketSuggestion msg,
        bool accepted,
        string? rejectReason,
        IDbContextTransaction? ownedTransaction,
        CancellationToken cancellationToken)
    {
        StageOutcome(msg, accepted, rejectReason);
        await db.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null)
            await ownedTransaction.CommitAsync(cancellationToken);
        return new Outcome(accepted, rejectReason);
    }
}
