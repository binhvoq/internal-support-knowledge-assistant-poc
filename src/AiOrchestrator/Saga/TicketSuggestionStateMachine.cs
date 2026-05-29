using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga.Timeouts;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Activities;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga;

public sealed class TicketSuggestionStateMachine : MassTransitStateMachine<TicketSuggestionState>
{
    public State Analyzing { get; private set; } = null!;
    public State RunningAi { get; private set; } = null!;
    public State Saving { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Failed { get; private set; } = null!;
    public State Compensated { get; private set; } = null!;

    public Event<ITicketCreated> TicketCreated { get; private set; } = null!;

    public Event<ITicketAnalyzingMarked> AnalyzingMarked { get; private set; } = null!;
    public Event<ITicketAnalyzingMarkFailed> AnalyzingMarkFailed { get; private set; } = null!;
    public Event<ITicketSuggestionSaved> SuggestionSaved { get; private set; } = null!;
    public Event<ITicketSuggestionSaveFailed> SuggestionSaveFailed { get; private set; } = null!;
    public Event<IMarkAnalyzingReverted> MarkAnalyzingReverted { get; private set; } = null!;

    public Event<IAiPipelineCompleted> AiPipelineCompleted { get; private set; } = null!;
    public Event<IAiPipelineFailed> AiPipelineFailed { get; private set; } = null!;

    public Schedule<TicketSuggestionState, ISagaTimeoutExpired> StepTimeout { get; private set; } = null!;
    public Schedule<TicketSuggestionState, ISagaVerifyDue> VerifyDue { get; private set; } = null!;

    internal readonly SagaTimeoutOptions TimeoutOptions;

    public TicketSuggestionStateMachine(IOptions<SagaTimeoutOptions> timeoutOptions)
    {
        TimeoutOptions = timeoutOptions.Value;
        var verifyDelay = TimeSpan.FromSeconds(Math.Max(5, TimeoutOptions.VerifyRetrySeconds));

        InstanceState(x => x.CurrentState);

        Event(() => TicketCreated, x =>
        {
            x.CorrelateById(ctx => ctx.Message.CorrelationId);
            x.InsertOnInitial = true;
            x.SetSagaFactory(ctx => new TicketSuggestionState
            {
                CorrelationId = ctx.Message.CorrelationId,
                TicketId = ctx.Message.TicketId,
                Question = ctx.Message.Question,
                EmployeeId = ctx.Message.EmployeeId,
                Category = ctx.Message.Category,
                TicketSagaEpoch = ctx.Message.SagaEpoch,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        });

        Event(() => AnalyzingMarked, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => AnalyzingMarkFailed, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => SuggestionSaved, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => SuggestionSaveFailed, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => MarkAnalyzingReverted, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => AiPipelineCompleted, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => AiPipelineFailed, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));

        Schedule(() => StepTimeout, x => x.TimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromSeconds(5);
            s.Received = r => r.CorrelateById(ctx => ctx.Message.CorrelationId);
        });

        Schedule(() => VerifyDue, x => x.VerifyTimeoutTokenId, s =>
        {
            s.Delay = verifyDelay;
            s.Received = r => r.CorrelateById(ctx => ctx.Message.CorrelationId);
        });

        Initially(
            When(TicketCreated)
                .Then(ctx =>
                {
                    ctx.Saga.OriginalStatus = TicketStatus.New;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .ScheduleStepTimeout(this, TimeoutOptions.Analyzing.TimeoutSeconds)
                .Send(ctx => new MarkTicketAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.TicketSagaEpoch))
                .TransitionTo(Analyzing));

        During(Analyzing,
            When(AnalyzingMarked)
                .Then(ctx =>
                {
                    ctx.Saga.TicketSagaEpoch = ctx.Message.SagaEpoch;
                    SagaTimeoutRecovery.ResetForNextStep(ctx.Saga);
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(this)
                .Unschedule(VerifyDue)
                .ScheduleStepTimeout(this, TimeoutOptions.RunningAi.TimeoutSeconds)
                .Send(ctx => new RunAiPipeline(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.TicketSagaEpoch,
                    ctx.Saga.Question,
                    ctx.Saga.Category ?? SupportCategory.Other))
                .TransitionTo(RunningAi),

            When(AnalyzingMarkFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.Reason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(this)
                .Unschedule(VerifyDue)
                .TransitionTo(Failed),

            When(StepTimeout.Received)
                .Activity(x => x.OfType<EvaluateAnalyzingTimeoutActivity>())
                .ApplyAnalyzingTimeoutOutcomeBranches(this),

            When(VerifyDue.Received)
                .Activity(x => x.OfType<EvaluateAnalyzingTimeoutActivity>())
                .ApplyAnalyzingTimeoutOutcomeBranches(this));

        During(RunningAi,
            When(AiPipelineCompleted)
                .Then(ctx =>
                {
                    ctx.Saga.Category = ctx.Message.Category;
                    ctx.Saga.Suggestion = ctx.Message.Suggestion;
                    ctx.Saga.RelatedDocumentsJson = JsonSerializer.Serialize(ctx.Message.RelatedDocuments);
                    SagaTimeoutRecovery.ResetForNextStep(ctx.Saga);
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(this)
                .Unschedule(VerifyDue)
                .ScheduleStepTimeout(this, TimeoutOptions.Saving.TimeoutSeconds)
                .Send(ctx => new SaveTicketSuggestion(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.TicketSagaEpoch,
                    ctx.Saga.Category!,
                    ctx.Saga.Suggestion!,
                    ctx.Message.RelatedDocuments))
                .TransitionTo(Saving),

            When(AiPipelineFailed)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = ctx.Message.Reason;
                    SagaTimeoutRecovery.ResetForCompensatingStep(ctx.Saga);
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(this)
                .Unschedule(VerifyDue)
                .ScheduleStepTimeout(this, TimeoutOptions.Compensating.TimeoutSeconds)
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(Compensating),

            When(StepTimeout.Received)
                .Activity(x => x.OfType<EvaluateRunningAiTimeoutActivity>())
                .ApplyRunningAiTimeoutOutcomeBranches(this),

            When(VerifyDue.Received)
                .Activity(x => x.OfType<EvaluateRunningAiTimeoutActivity>())
                .ApplyRunningAiTimeoutOutcomeBranches(this));

        During(Saving,
            When(SuggestionSaved)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow)
                .UnscheduleAllStepTimeouts(this)
                .Unschedule(VerifyDue)
                .PublishAsync(ctx => ctx.Init<IAiSuggestionGenerated>(
                    new AiSuggestionGenerated(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))
                .TransitionTo(Completed),

            When(SuggestionSaveFailed)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = ctx.Message.Reason;
                    SagaTimeoutRecovery.ResetForCompensatingStep(ctx.Saga);
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(this)
                .Unschedule(VerifyDue)
                .ScheduleStepTimeout(this, TimeoutOptions.Compensating.TimeoutSeconds)
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(Compensating),

            When(StepTimeout.Received)
                .Activity(x => x.OfType<EvaluateSavingTimeoutActivity>())
                .ApplySavingTimeoutOutcomeBranches(this),

            When(VerifyDue.Received)
                .Activity(x => x.OfType<EvaluateSavingTimeoutActivity>())
                .ApplySavingTimeoutOutcomeBranches(this));

        During(Compensating,
            When(MarkAnalyzingReverted)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow)
                .UnscheduleAllStepTimeouts(this)
                .Unschedule(VerifyDue)
                .TransitionTo(Compensated),

            When(StepTimeout.Received)
                .Activity(x => x.OfType<EvaluateCompensatingTimeoutActivity>())
                .ApplyCompensatingTimeoutOutcomeBranches(this),

            When(VerifyDue.Received)
                .Activity(x => x.OfType<EvaluateCompensatingTimeoutActivity>())
                .ApplyCompensatingTimeoutOutcomeBranches(this));

        DuringAny(
            Ignore(MarkAnalyzingReverted),
            Ignore(AnalyzingMarked),
            Ignore(AnalyzingMarkFailed),
            Ignore(SuggestionSaved),
            Ignore(SuggestionSaveFailed),
            Ignore(AiPipelineCompleted),
            Ignore(AiPipelineFailed),
            Ignore(StepTimeout.Received),
            Ignore(VerifyDue.Received));
    }
}

internal static class SagaTimeoutScheduleExtensions
{
    public static EventActivityBinder<TicketSuggestionState, TMessage> ScheduleStepTimeout<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine,
        int timeoutSeconds)
        where TMessage : class =>
        binder.Schedule(
            machine.StepTimeout,
            ctx => new SagaTimeoutExpired(ctx.Saga.CorrelationId, ctx.Saga.TicketId),
            ctx => TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

    public static EventActivityBinder<TicketSuggestionState, TMessage> UnscheduleAllStepTimeouts<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine)
        where TMessage : class =>
        binder.Unschedule(machine.StepTimeout);
}

internal static class CompensatingTimeoutStateMachineExtensions
{
    public static EventActivityBinder<TicketSuggestionState, TMessage> ApplyCompensatingTimeoutOutcomeBranches<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine)
        where TMessage : class
    {
        return binder
            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Complete), b => b
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.Compensated))

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
                    ctx.Saga.OriginalStatus))
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Fail), b => b
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.Failed));
    }
}

internal static class AnalyzingTimeoutStateMachineExtensions
{
    public static EventActivityBinder<TicketSuggestionState, TMessage> ApplyAnalyzingTimeoutOutcomeBranches<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine)
        where TMessage : class
    {
        return binder
            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Proceed), b => b
                .Then(ctx =>
                {
                    SagaTimeoutRecovery.ResetForNextStep(ctx.Saga);
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .ScheduleStepTimeout(machine, machine.TimeoutOptions.RunningAi.TimeoutSeconds)
                .Send(ctx => new RunAiPipeline(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.TicketSagaEpoch,
                    ctx.Saga.Question,
                    ctx.Saga.Category ?? SupportCategory.Other))
                .TransitionTo(machine.RunningAi))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.RetryVerify), b => b
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.ResendMark), b => b
                .Then(ctx =>
                {
                    ctx.Saga.MarkResendIssued = true;
                    ctx.Saga.MarkResendIssuedAt = DateTimeOffset.UtcNow;
                    ctx.Saga.PostResendVerifyAttempts = 0;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => new MarkTicketAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.TicketSagaEpoch))
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Fail), b => b
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.Failed));
    }
}

internal static class RunningAiTimeoutStateMachineExtensions
{
    public static EventActivityBinder<TicketSuggestionState, TMessage> ApplyRunningAiTimeoutOutcomeBranches<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine)
        where TMessage : class
    {
        return binder
            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Proceed), b => b
                .Then(ctx =>
                {
                    SagaTimeoutRecovery.ResetForNextStep(ctx.Saga);
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .ScheduleStepTimeout(machine, machine.TimeoutOptions.Saving.TimeoutSeconds)
                .Send(ctx => new SaveTicketSuggestion(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.TicketSagaEpoch,
                    ctx.Saga.Category!,
                    ctx.Saga.Suggestion!,
                    SagaSaveCommandFactory.DeserializeRelatedDocuments(ctx.Saga.RelatedDocumentsJson)))
                .TransitionTo(machine.Saving))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.RetryVerify), b => b
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.ResendRun), b => b
                .Then(ctx =>
                {
                    ctx.Saga.AiRunResendCount++;
                    ctx.Saga.AiRunResendIssuedAt = DateTimeOffset.UtcNow;
                    ctx.Saga.PostResendVerifyAttempts = 0;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => SagaRunCommandFactory.Create(ctx.Saga))
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Compensate), b => b
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Then(ctx => SagaTimeoutRecovery.ResetForCompensatingStep(ctx.Saga))
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .ScheduleStepTimeout(machine, machine.TimeoutOptions.Compensating.TimeoutSeconds)
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(machine.Compensating))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Fail), b => b
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.Failed));
    }
}

internal static class SavingTimeoutStateMachineExtensions
{
    public static EventActivityBinder<TicketSuggestionState, TMessage> ApplySavingTimeoutOutcomeBranches<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine)
        where TMessage : class
    {
        return binder
            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Complete), b => b
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .PublishAsync(ctx => ctx.Init<IAiSuggestionGenerated>(
                    new AiSuggestionGenerated(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))
                .TransitionTo(machine.Completed))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.RetryVerify), b => b
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.ResendSave), b => b
                .Then(ctx =>
                {
                    ctx.Saga.SaveResendIssued = true;
                    ctx.Saga.SaveResendIssuedAt = DateTimeOffset.UtcNow;
                    ctx.Saga.PostResendVerifyAttempts = 0;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => SagaSaveCommandFactory.Create(ctx.Saga))
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Compensate), b => b
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Then(ctx => SagaTimeoutRecovery.ResetForCompensatingStep(ctx.Saga))
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .ScheduleStepTimeout(machine, machine.TimeoutOptions.Compensating.TimeoutSeconds)
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(machine.Compensating))

            .If(ctx => SagaTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Fail), b => b
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .UnscheduleAllStepTimeouts(machine)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.Failed));
    }
}
