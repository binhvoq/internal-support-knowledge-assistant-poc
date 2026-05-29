using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Policies;

public sealed class RunningAiTimeoutPolicy(IOptions<SagaTimeoutOptions> options)
{
    public SagaTimeoutDecision Decide(TicketProgressProbeResult probe, StepTimeoutContext ctx)
    {
        var step = options.Value.RunningAi;

        if (HasAiPayload(ctx.Saga))
            return SagaTimeoutDecision.Proceed("AI payload already present on saga state", ctx.Saga.TicketSagaEpoch);

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

        if (ticket.ActiveSagaCorrelationId != saga.CorrelationId)
        {
            return SagaTimeoutDecision.Fail(
                $"Ticket owned by another saga: active={ticket.ActiveSagaCorrelationId}, expected={saga.CorrelationId}");
        }

        if (ticket.SagaEpoch != saga.TicketSagaEpoch)
        {
            return SagaTimeoutDecision.Fail(
                $"Unexpected epoch: ticket={ticket.SagaEpoch}, saga={saga.TicketSagaEpoch}");
        }

        if (ticket.Status is TicketStatus.Suggested or TicketStatus.Resolved)
        {
            return SagaTimeoutDecision.Fail(
                $"Unexpected terminal status during RunningAi: {ticket.Status}");
        }

        if (ticket.Status != TicketStatus.Analyzing)
            return SagaTimeoutDecision.Fail($"Unexpected ticket status: {ticket.Status}");

        if (ticket.HasSuggestion)
            return SagaTimeoutDecision.Fail("Analyzing but HasSuggestion=true — inconsistent state");

        if (ctx.ResendCount > 0)
        {
            if (ctx.PostResendVerifyAttempt < step.PostResendVerifyAttempts)
            {
                return SagaTimeoutDecision.RetryVerify(
                    $"Post-resend AI verify {ctx.PostResendVerifyAttempt + 1}/{step.PostResendVerifyAttempts}");
            }

            if (ctx.ResendCount < step.MaxResendAttempts)
                return SagaTimeoutDecision.ResendRun("Post-resend grace exhausted; resend RunAiPipeline");

            return SagaTimeoutDecision.Compensate(
                $"AI resend limit ({step.MaxResendAttempts}) exhausted; payload still missing");
        }

        if (ctx.VerifyAttempt < step.MaxVerifyAttempts)
        {
            return SagaTimeoutDecision.RetryVerify(
                $"Pre-resend AI verify {ctx.VerifyAttempt + 1}/{step.MaxVerifyAttempts}");
        }

        return SagaTimeoutDecision.ResendRun("Pre-resend verify attempts exhausted; resend RunAiPipeline");
    }

    private static bool HasAiPayload(TicketSuggestionState saga) =>
        !string.IsNullOrWhiteSpace(saga.Category) && !string.IsNullOrWhiteSpace(saga.Suggestion);
}
