using MassTransit;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga;

internal static class RevertBeforeFailedExtensions
{
    public const string DefaultStopNote =
        "AI suggestion saga stopped; ticket reverted to initial status. Late worker messages are ignored.";

    public static string FormatStopNote(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? DefaultStopNote : $"{DefaultStopNote} ({reason})";

    public static EventActivityBinder<TicketSuggestionState, TMessage> ApplyRevertBeforeFailed<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine,
        Func<BehaviorContext<TicketSuggestionState, TMessage>, string?>? failureReason = null)
        where TMessage : class
    {
        return binder
            .Then(ctx =>
            {
                var reason = failureReason?.Invoke(ctx) ?? ctx.Saga.TimeoutDecisionReason ?? ctx.Saga.FailureReason;
                ctx.Saga.FailureReason = reason;
                SagaTimeoutRecovery.ResetForCompensatingStep(ctx.Saga);
                ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
            })
            .UnscheduleAllStepTimeouts(machine)
            .Unschedule(machine.VerifyDue)
            .ScheduleStepTimeout(machine, machine.TimeoutOptions.Compensating.TimeoutSeconds)
            .Send(ctx => new CompensateMarkAnalyzing(
                ctx.Saga.CorrelationId,
                ctx.Saga.TicketId,
                string.IsNullOrWhiteSpace(ctx.Saga.OriginalStatus) ? TicketStatus.New : ctx.Saga.OriginalStatus,
                FormatStopNote(ctx.Saga.FailureReason)))
            .TransitionTo(machine.RevertingBeforeFailed);
    }

    public static EventActivityBinder<TicketSuggestionState, TMessage> ApplyRevertingBeforeFailedTimeoutOutcomeBranches<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine)
        where TMessage : class
    {
        return binder
            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Complete), b => b
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.Failed))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.RetryVerify), b => b
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.ResendCompensate), b => b
                .Then(ctx =>
                {
                    ctx.Saga.CompensateResendCount++;
                    ctx.Saga.CompensateResendIssuedAt = DateTimeOffset.UtcNow;
                    ctx.Saga.PostResendVerifyAttempts = 0;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    string.IsNullOrWhiteSpace(ctx.Saga.OriginalStatus) ? TicketStatus.New : ctx.Saga.OriginalStatus,
                    FormatStopNote(ctx.Saga.FailureReason)))
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Fail), b => b
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = $"Failed to confirm ticket rollback before Failed: {ctx.Saga.TimeoutDecisionReason}";
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.RollbackFailed));
    }
}
