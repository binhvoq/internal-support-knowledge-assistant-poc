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

    public TicketSuggestionStateMachine(IOptions<SagaTimeoutOptions> timeoutOptions)
    {
        var opts = timeoutOptions.Value;
        var stepTimeoutDelay = TimeSpan.FromSeconds(Math.Max(5, opts.TimeoutSeconds));
        var verifyDelay = TimeSpan.FromSeconds(Math.Max(5, opts.VerifyRetrySeconds));

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
            s.Delay = stepTimeoutDelay;
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
                .Schedule(StepTimeout, ctx => new SagaTimeoutExpired(ctx.Saga.CorrelationId, ctx.Saga.TicketId))
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
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => new RunAiPipeline(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.Question,
                    ctx.Saga.Category ?? SupportCategory.Other))
                .TransitionTo(RunningAi),

            When(AnalyzingMarkFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.Reason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Unschedule(StepTimeout)
                .TransitionTo(Failed),

            When(StepTimeout.Received)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = "Timeout cho TicketService confirm MarkAnalyzing.";
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(Compensating));

        During(RunningAi,
            When(AiPipelineCompleted)
                .Then(ctx =>
                {
                    ctx.Saga.Category = ctx.Message.Category;
                    ctx.Saga.Suggestion = ctx.Message.Suggestion;
                    ctx.Saga.RelatedDocumentsJson = JsonSerializer.Serialize(ctx.Message.RelatedDocuments);
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
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
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(Compensating),

            When(StepTimeout.Received)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = "Timeout khi cho AI pipeline.";
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(Compensating));

        During(Saving,
            When(SuggestionSaved)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow)
                .Unschedule(StepTimeout)
                .Unschedule(VerifyDue)
                .PublishAsync(ctx => ctx.Init<IAiSuggestionGenerated>(
                    new AiSuggestionGenerated(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))
                .TransitionTo(Completed),

            When(SuggestionSaveFailed)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = ctx.Message.Reason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Unschedule(VerifyDue)
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
                .Unschedule(StepTimeout)
                .Unschedule(VerifyDue)
                .TransitionTo(Compensated),

            When(StepTimeout.Received)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = $"Compensation cung timeout: {ctx.Saga.CompensationReason}";
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .TransitionTo(Failed));

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

internal static class SavingTimeoutStateMachineExtensions
{
    public static EventActivityBinder<TicketSuggestionState, TMessage> ApplySavingTimeoutOutcomeBranches<TMessage>(
        this EventActivityBinder<TicketSuggestionState, TMessage> binder,
        TicketSuggestionStateMachine machine)
        where TMessage : class
    {
        return binder
            .If(ctx => SavingTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Complete), b => b
                .Unschedule(machine.StepTimeout)
                .Unschedule(machine.VerifyDue)
                .PublishAsync(ctx => ctx.Init<IAiSuggestionGenerated>(
                    new AiSuggestionGenerated(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))
                .TransitionTo(machine.Completed))

            .If(ctx => SavingTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.RetryVerify), b => b
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SavingTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.ResendSave), b => b
                .Then(ctx =>
                {
                    ctx.Saga.SaveResendIssued = true;
                    ctx.Saga.SaveResendIssuedAt = DateTimeOffset.UtcNow;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => SagaSaveCommandFactory.Create(ctx.Saga))
                .Schedule(machine.VerifyDue, ctx => new SagaVerifyDue(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))

            .If(ctx => SavingTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Compensate), b => b
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Unschedule(machine.StepTimeout)
                .Unschedule(machine.VerifyDue)
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(machine.Compensating))

            .If(ctx => SavingTimeoutOutcomeHelper.Is(ctx, SagaTimeoutOutcome.Fail), b => b
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Saga.TimeoutDecisionReason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Unschedule(machine.StepTimeout)
                .Unschedule(machine.VerifyDue)
                .TransitionTo(machine.Failed));
    }
}
