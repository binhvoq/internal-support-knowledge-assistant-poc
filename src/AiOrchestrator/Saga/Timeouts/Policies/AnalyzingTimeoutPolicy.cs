using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Policies;

public sealed class AnalyzingTimeoutPolicy(IOptions<SagaTimeoutOptions> options)
{
    public SagaTimeoutDecision Decide(TicketProgressProbeResult probe, StepTimeoutContext ctx)
    {
        var step = options.Value.Analyzing;

        return probe.Status switch
        {
            TicketProgressProbeStatus.NotFound =>
                SagaTimeoutDecision.Fail($"Ticket not found: {probe.Error}"),

            TicketProgressProbeStatus.Unavailable =>
                ctx.VerifyAttempt < step.MaxVerifyAttempts
                    ? SagaTimeoutDecision.RetryVerify($"TicketService unavailable: {probe.Error}")
                    : SagaTimeoutDecision.Fail($"Probe unavailable after {step.MaxVerifyAttempts} attempts: {probe.Error}"),

            TicketProgressProbeStatus.InvalidResponse =>
                SagaTimeoutDecision.Fail($"Invalid probe response: {probe.Error}"),

            TicketProgressProbeStatus.Found => DecideFromSnapshot(probe.Snapshot!, ctx, step),

            _ => SagaTimeoutDecision.Fail($"Unknown probe status: {probe.Status}")
        };
    }

    private static SagaTimeoutDecision DecideFromSnapshot(
        TicketProgressSnapshot ticket,
        StepTimeoutContext ctx,
        SagaStepTimeoutOptions step)
    {
        var saga = ctx.Saga;

        if (TryGetMarkedEpoch(ticket, saga, out var markedEpoch))
            return SagaTimeoutDecision.Proceed(
                "MarkAnalyzing already applied at TicketService (source of truth)",
                markedEpoch);

        if (ticket.ActiveSagaCorrelationId is not null &&
            ticket.ActiveSagaCorrelationId != saga.CorrelationId)
        {
            return SagaTimeoutDecision.Fail(
                $"Ticket owned by another saga: active={ticket.ActiveSagaCorrelationId}, expected={saga.CorrelationId}");
        }

        if (ticket.Status is TicketStatus.Suggested or TicketStatus.Resolved)
        {
            return SagaTimeoutDecision.Fail(
                $"Unexpected terminal status during Analyzing: {ticket.Status}");
        }

        if (ticket.SagaEpoch != saga.TicketSagaEpoch)
        {
            return SagaTimeoutDecision.Fail(
                $"Unexpected epoch for mark: ticket={ticket.SagaEpoch}, saga expected={saga.TicketSagaEpoch}");
        }

        if (ticket.Status != TicketStatus.New && ticket.Status != TicketStatus.Analyzing)
            return SagaTimeoutDecision.Fail($"Unexpected ticket status: {ticket.Status}");

        if (ctx.ResendCount > 0)
        {
            if (ctx.PostResendVerifyAttempt < step.PostResendVerifyAttempts)
            {
                return SagaTimeoutDecision.RetryVerify(
                    $"Post-resend mark verify {ctx.PostResendVerifyAttempt + 1}/{step.PostResendVerifyAttempts}");
            }

            return SagaTimeoutDecision.Fail("Post-resend grace exhausted; mark still not visible at source of truth");
        }

        if (ctx.VerifyAttempt < step.MaxVerifyAttempts)
        {
            return SagaTimeoutDecision.RetryVerify(
                $"Pre-resend mark verify {ctx.VerifyAttempt + 1}/{step.MaxVerifyAttempts}");
        }

        return SagaTimeoutDecision.ResendMark("Pre-resend verify attempts exhausted; resend MarkTicketAnalyzing");
    }

    private static bool TryGetMarkedEpoch(
        TicketProgressSnapshot ticket,
        TicketSuggestionState saga,
        out int markedEpoch)
    {
        markedEpoch = 0;

        if (ticket.Status != TicketStatus.Analyzing ||
            ticket.ActiveSagaCorrelationId != saga.CorrelationId)
        {
            return false;
        }

        if (ticket.SagaEpoch == saga.TicketSagaEpoch)
        {
            markedEpoch = ticket.SagaEpoch;
            return true;
        }

        if (ticket.SagaEpoch == saga.TicketSagaEpoch + 1)
        {
            markedEpoch = ticket.SagaEpoch;
            return true;
        }

        return false;
    }
}
