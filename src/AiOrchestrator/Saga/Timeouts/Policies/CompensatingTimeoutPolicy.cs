using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Policies;

public sealed class CompensatingTimeoutPolicy(IOptions<SagaTimeoutOptions> options)
{
    public SagaTimeoutDecision Decide(TicketProgressProbeResult probe, StepTimeoutContext ctx)
    {
        var step = options.Value.Compensating;

        return probe.Status switch
        {
            TicketProgressProbeStatus.NotFound =>
                SagaTimeoutDecision.Fail($"Ticket not found during compensation verify: {probe.Error}"),

            TicketProgressProbeStatus.Unavailable =>
                ctx.VerifyAttempt < step.MaxVerifyAttempts
                    ? SagaTimeoutDecision.RetryVerify($"TicketService unavailable during compensation verify: {probe.Error}")
                    : SagaTimeoutDecision.Fail(
                        $"Unable to verify compensation state after {step.MaxVerifyAttempts} attempts: {probe.Error}"),

            TicketProgressProbeStatus.InvalidResponse =>
                SagaTimeoutDecision.Fail($"Invalid probe response during compensation: {probe.Error}"),

            TicketProgressProbeStatus.Found => DecideFromSnapshot(probe.Snapshot!, ctx, step),

            _ => SagaTimeoutDecision.Fail($"Unknown probe status during compensation: {probe.Status}")
        };
    }

    private static SagaTimeoutDecision DecideFromSnapshot(
        TicketProgressSnapshot ticket,
        StepTimeoutContext ctx,
        SagaStepTimeoutOptions step)
    {
        var saga = ctx.Saga;

        if (IsReverted(ticket, saga))
        {
            return SagaTimeoutDecision.Complete(
                "Compensation already applied at TicketService (source of truth)");
        }

        if (ticket.ActiveSagaCorrelationId is not null &&
            ticket.ActiveSagaCorrelationId != saga.CorrelationId)
        {
            return SagaTimeoutDecision.Fail(
                $"Ticket owned by another saga during compensation: active={ticket.ActiveSagaCorrelationId}, expected={saga.CorrelationId}");
        }

        if (ticket.Status is TicketStatus.Suggested or TicketStatus.Resolved)
        {
            return SagaTimeoutDecision.Fail(
                $"Unexpected terminal status during compensation: {ticket.Status}");
        }

        if (!StillNeedsCompensation(ticket, saga))
        {
            return SagaTimeoutDecision.Fail(
                $"Unexpected ticket snapshot during compensation: status={ticket.Status}, epoch={ticket.SagaEpoch}, hasSuggestion={ticket.HasSuggestion}");
        }

        if (ctx.ResendCount > 0)
        {
            if (ctx.PostResendVerifyAttempt < step.PostResendVerifyAttempts)
            {
                return SagaTimeoutDecision.RetryVerify(
                    $"Post-resend compensation verify {ctx.PostResendVerifyAttempt + 1}/{step.PostResendVerifyAttempts}");
            }

            if (ctx.ResendCount < step.MaxResendAttempts)
                return SagaTimeoutDecision.ResendCompensate("Post-resend grace exhausted; resend CompensateMarkAnalyzing");

            return SagaTimeoutDecision.Fail(
                $"Compensation resend limit ({step.MaxResendAttempts}) exhausted; ticket still owned and Analyzing");
        }

        if (ctx.VerifyAttempt < step.MaxVerifyAttempts)
        {
            return SagaTimeoutDecision.RetryVerify(
                $"Pre-resend compensation verify {ctx.VerifyAttempt + 1}/{step.MaxVerifyAttempts}");
        }

        return SagaTimeoutDecision.ResendCompensate(
            "Pre-resend verify attempts exhausted; resend CompensateMarkAnalyzing");
    }

    private static bool IsReverted(TicketProgressSnapshot ticket, TicketSuggestionState saga)
    {
        var targetStatus = string.IsNullOrWhiteSpace(saga.OriginalStatus) ? TicketStatus.New : saga.OriginalStatus;
        if (ticket.Status != targetStatus)
            return false;

        if (ticket.HasSuggestion)
            return false;

        return ticket.ActiveSagaCorrelationId != saga.CorrelationId;
    }

    private static bool StillNeedsCompensation(TicketProgressSnapshot ticket, TicketSuggestionState saga) =>
        ticket.ActiveSagaCorrelationId == saga.CorrelationId
        && ticket.Status == TicketStatus.Analyzing
        && !ticket.HasSuggestion;
}
