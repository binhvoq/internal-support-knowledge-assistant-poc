using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Policies;

public sealed class SavingTimeoutPolicy(IOptions<SagaTimeoutOptions> options)
{
    public SagaTimeoutDecision Decide(TicketProgressProbeResult probe, SagaTimeoutContext ctx)
    {
        var saga = ctx.Saga;
        var opts = options.Value;

        return probe.Status switch
        {
            TicketProgressProbeStatus.NotFound =>
                SagaTimeoutDecision.Fail($"Ticket not found: {probe.Error}"),

            TicketProgressProbeStatus.Unavailable =>
                ctx.VerifyAttempt < opts.MaxVerifyAttempts
                    ? SagaTimeoutDecision.RetryVerify($"TicketService unavailable: {probe.Error}")
                    : SagaTimeoutDecision.Fail($"Probe unavailable after {opts.MaxVerifyAttempts} attempts: {probe.Error}"),

            TicketProgressProbeStatus.InvalidResponse =>
                SagaTimeoutDecision.Fail($"Invalid probe response: {probe.Error}"),

            TicketProgressProbeStatus.Found => DecideFromSnapshot(probe.Snapshot!, ctx, opts),

            _ => SagaTimeoutDecision.Fail($"Unknown probe status: {probe.Status}")
        };
    }

    private static SagaTimeoutDecision DecideFromSnapshot(
        TicketProgressSnapshot ticket,
        SagaTimeoutContext ctx,
        SagaTimeoutOptions opts)
    {
        var saga = ctx.Saga;

        if (IsSavedForThisSaga(ticket, saga))
            return SagaTimeoutDecision.Complete("Save already applied at TicketService (source of truth)");

        if (ticket.ActiveSagaCorrelationId != saga.CorrelationId)
            return SagaTimeoutDecision.Fail(
                $"Ticket owned by another saga: active={ticket.ActiveSagaCorrelationId}, expected={saga.CorrelationId}");

        if (!IsExpectedEpoch(ticket, saga))
            return SagaTimeoutDecision.Fail(
                $"Unexpected epoch: ticket={ticket.SagaEpoch}, saga={saga.TicketSagaEpoch}");

        if (ticket.Status is TicketStatus.Suggested or TicketStatus.Resolved)
            return SagaTimeoutDecision.Fail(
                $"Unexpected status after ownership match: {ticket.Status} without matching suggestion snapshot");

        if (ticket.Status != TicketStatus.Analyzing)
            return SagaTimeoutDecision.Fail($"Unexpected ticket status: {ticket.Status}");

        if (ticket.HasSuggestion)
            return SagaTimeoutDecision.Fail("Analyzing but HasSuggestion=true — inconsistent state");

        if (ctx.SaveResendIssued)
        {
            if (ctx.PostResendVerifyAttempt < opts.PostResendVerifyAttempts)
                return SagaTimeoutDecision.RetryVerify(
                    $"Post-resend verify {ctx.PostResendVerifyAttempt + 1}/{opts.PostResendVerifyAttempts}");

            return SagaTimeoutDecision.Compensate(
                "Post-resend grace exhausted; save still not visible at source of truth");
        }

        if (ctx.VerifyAttempt < opts.MaxVerifyAttempts)
            return SagaTimeoutDecision.RetryVerify(
                $"Pre-resend verify {ctx.VerifyAttempt + 1}/{opts.MaxVerifyAttempts}");

        if (!SagaSaveCommandFactory.CanCreate(saga, out var reason))
            return SagaTimeoutDecision.Fail(reason);

        return SagaTimeoutDecision.ResendSave("Pre-resend verify attempts exhausted; resend SaveTicketSuggestion");
    }

    private static bool IsSavedForThisSaga(TicketProgressSnapshot ticket, TicketSuggestionState saga) =>
        ticket.Status == TicketStatus.Suggested
        && ticket.SagaEpoch == saga.TicketSagaEpoch
        && ticket.ActiveSagaCorrelationId == saga.CorrelationId
        && ticket.HasSuggestion;

    private static bool IsExpectedEpoch(TicketProgressSnapshot ticket, TicketSuggestionState saga) =>
        ticket.SagaEpoch == saga.TicketSagaEpoch;
}
