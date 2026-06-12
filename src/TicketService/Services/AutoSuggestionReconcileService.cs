using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

public sealed class AutoSuggestionReconcileService(TicketDbContext db)
{
    public async Task<AutoSuggestionReconcileResult> ReconcileAsync(
        string ticketId,
        Guid jobId,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (await WasAcceptedBySameJobAsync(ticketId, jobId, cancellationToken))
        {
            return await BuildAcceptedBySameJobResultAsync(ticketId, jobId, cancellationToken);
        }

        var ticket = await db.Tickets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

        if (ticket is null)
        {
            return new AutoSuggestionReconcileResult(
                ticketId,
                jobId,
                AutoSuggestionReconcileDecision.NotFound,
                "Ticket not found.",
                null,
                null,
                false,
                false);
        }

        if (!string.IsNullOrWhiteSpace(ticket.FinalAnswer) || ticket.Status is TicketStatus.Resolved)
        {
            return ToResult(
                ticket,
                jobId,
                AutoSuggestionReconcileDecision.Resolved,
                AutoSuggestionRules.GetRejectReason(ticket, expectedVersion)
                ?? "Ticket already has a final answer or is resolved.");
        }

        if (!string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
        {
            return await ResolveExistingSuggestionAsync(ticket, jobId, cancellationToken);
        }

        if (expectedVersion is not null
            && ticket.Version != expectedVersion
            && !IsInitialState(ticket))
        {
            return ToResult(
                ticket,
                jobId,
                AutoSuggestionReconcileDecision.VersionChanged,
                AutoSuggestionRules.GetRejectReason(ticket, expectedVersion)
                ?? "Ticket version mismatch (stale command).");
        }

        if (AutoSuggestionRules.CanAccept(ticket, expectedVersion))
        {
            return ToResult(
                ticket,
                jobId,
                AutoSuggestionReconcileDecision.StillSuggestible,
                null);
        }

        var rejectReason = AutoSuggestionRules.GetRejectReason(ticket, expectedVersion)
            ?? "Ticket is not eligible for auto suggestion.";

        var decision = ticket.Status is TicketStatus.Reopened
            ? AutoSuggestionReconcileDecision.Resolved
            : rejectReason.Contains("version mismatch", StringComparison.OrdinalIgnoreCase)
                && !IsInitialState(ticket)
                ? AutoSuggestionReconcileDecision.VersionChanged
                : AutoSuggestionReconcileDecision.Resolved;

        return ToResult(ticket, jobId, decision, rejectReason);
    }

    private async Task<AutoSuggestionReconcileResult> BuildAcceptedBySameJobResultAsync(
        string ticketId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

        const string reason = "Proposal already accepted for this job.";
        return ticket is null
            ? new AutoSuggestionReconcileResult(
                ticketId,
                jobId,
                AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob,
                reason,
                null,
                null,
                true,
                false)
            : ToResult(ticket, jobId, AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob, reason);
    }

    private async Task<AutoSuggestionReconcileResult> ResolveExistingSuggestionAsync(
        TicketEntity ticket,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (await WasAcceptedBySameJobAsync(ticket.Id, jobId, cancellationToken))
        {
            return ToResult(
                ticket,
                jobId,
                AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob,
                "Proposal already accepted for this job.");
        }

        var acceptedJobId = await db.ProcessedCommands
            .Where(c => c.TicketId == ticket.Id && c.Accepted)
            .OrderByDescending(c => c.ProcessedAt)
            .Select(c => (Guid?)c.JobId)
            .FirstOrDefaultAsync(cancellationToken);

        if (acceptedJobId == jobId)
        {
            return ToResult(
                ticket,
                jobId,
                AutoSuggestionReconcileDecision.AlreadyAppliedBySameJob,
                "Proposal already accepted for this job.");
        }

        var reason = acceptedJobId is not null
            ? $"Ticket already has an accepted AI suggestion from job {acceptedJobId}."
            : "Ticket already has an AI suggestion without a matching command record.";

        return ToResult(
            ticket,
            jobId,
            AutoSuggestionReconcileDecision.AlreadySuggestedByOtherJob,
            reason);
    }

    private Task<bool> WasAcceptedBySameJobAsync(string ticketId, Guid jobId, CancellationToken cancellationToken) =>
        db.ProcessedCommands.AnyAsync(
            c => c.TicketId == ticketId && c.JobId == jobId && c.Accepted,
            cancellationToken);

    private static bool IsInitialState(TicketEntity ticket) =>
        ticket.Status == TicketStatus.New
        && string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer)
        && string.IsNullOrWhiteSpace(ticket.FinalAnswer);

    private static AutoSuggestionReconcileResult ToResult(
        TicketEntity ticket,
        Guid jobId,
        string decision,
        string? reason) =>
        new(
            ticket.Id,
            jobId,
            decision,
            reason,
            ticket.Status,
            ticket.Version,
            !string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer),
            !string.IsNullOrWhiteSpace(ticket.FinalAnswer));
}
