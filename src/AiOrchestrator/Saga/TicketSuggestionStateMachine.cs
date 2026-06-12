using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Contracts;

namespace SupportPoc.AiOrchestrator.Saga;

public sealed class TicketSuggestionStateMachine : MassTransitStateMachine<TicketSuggestionSaga>
{
    private static readonly Uri GenerateSuggestionEndpoint = new("queue:generate-suggestion-requested");

    public State GeneratingSuggestion { get; private set; } = null!;
    public State ApplyingSuggestion { get; private set; } = null!;
    public State Reconciling { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Discarded { get; private set; } = null!;
    public State Failed { get; private set; } = null!;

    public Event<ITicketCreated> TicketCreated { get; private set; } = null!;
    public Event<ISuggestionGenerated> SuggestionGenerated { get; private set; } = null!;
    public Event<ISuggestionGenerationFailed> SuggestionGenerationFailed { get; private set; } = null!;

    public Schedule<TicketSuggestionSaga, IStepTimeout> StepTimeoutSchedule { get; private set; } = null!;

    public Request<TicketSuggestionSaga, IProposeTicketSuggestion, IProposeTicketSuggestionResult> ProposeSuggestion { get; private set; } = null!;

    public TicketSuggestionStateMachine(IOptions<AutoSuggestionOptions> options)
    {
        var opts = options.Value;
        var stepTimeout = TimeSpan.FromSeconds(Math.Max(5, opts.StepTimeoutSeconds));
        var proposeTimeout = TimeSpan.FromSeconds(Math.Max(5, opts.ProposeRequestTimeoutSeconds));

        InstanceState(x => x.CurrentState);

        Event(() => TicketCreated, e => e.CorrelateById(m => m.Message.JobId));
        Event(() => SuggestionGenerated, e =>
        {
            e.CorrelateById(m => m.Message.SagaId);
            e.OnMissingInstance(m => m.Execute(ctx =>
                TicketSuggestionActivities.AuditMissingSagaInstance(ctx, nameof(ISuggestionGenerated))));
        });
        Event(() => SuggestionGenerationFailed, e =>
        {
            e.CorrelateById(m => m.Message.SagaId);
            e.OnMissingInstance(m => m.Execute(ctx =>
                TicketSuggestionActivities.AuditMissingSagaInstance(ctx, nameof(ISuggestionGenerationFailed))));
        });

        Schedule(() => StepTimeoutSchedule, x => x.StepTimeoutTokenId, s =>
        {
            s.Delay = stepTimeout;
            s.Received = r => r.CorrelateById(m => m.Message.SagaId);
        });

        Request(() => ProposeSuggestion, r =>
        {
            r.ServiceAddress = new Uri("queue:propose-ticket-suggestion");
            r.Timeout = proposeTimeout;
        });

        Initially(
            When(TicketCreated)
                .Then(TicketSuggestionActivities.InitializeSaga)
                .Then(TicketSuggestionActivities.StartNewAttempt)
                .Send(GenerateSuggestionEndpoint, ctx => TicketSuggestionActivities.CreateGenerateRequest(ctx))
                .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                .TransitionTo(GeneratingSuggestion));

        During(GeneratingSuggestion,
            Ignore(TicketCreated),
            When(SuggestionGenerated)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => then
                    .Then(TicketSuggestionActivities.StoreGeneratedResult)
                    .Unschedule(StepTimeoutSchedule)
                    .Request(ProposeSuggestion, ctx => TicketSuggestionActivities.CreateProposeRequest(ctx))
                    .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                    .TransitionTo(ApplyingSuggestion))
                .If(ctx => ctx.Message.AttemptId != ctx.Saga.CurrentAttemptId, then => then
                    .Then(TicketSuggestionActivities.AuditLateAttempt)),

            When(SuggestionGenerationFailed)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.Unschedule(StepTimeoutSchedule)
                        .TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.Reconcile))),

            When(StepTimeoutSchedule.Received)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.Reconcile))));

        During(ApplyingSuggestion,
            Ignore(TicketCreated),
            When(ProposeSuggestion.Completed)
                .Unschedule(StepTimeoutSchedule)
                .If(ctx => ctx.Message.Accepted, accepted => accepted
                    .Publish(ctx => new AiSuggestionGenerated(ctx.Saga.JobId, ctx.Saga.TicketId))
                    .TransitionTo(Completed)
                    )
                .If(ctx => !ctx.Message.Accepted, rejected => rejected
                    .Then(ctx =>
                    {
                        ctx.Saga.DiscardReason = ctx.Message.Reason;
                        ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    })
                    .Publish(ctx => new AutoSuggestionDiscarded(ctx.Saga.JobId, ctx.Saga.TicketId, ctx.Saga.DiscardReason ?? "Rejected"))
                    .TransitionTo(Discarded)
                    ),

            When(ProposeSuggestion.Faulted)
                .Unschedule(StepTimeoutSchedule)
                .If(_ => true, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.Reconcile))),

            When(ProposeSuggestion.TimeoutExpired)
                .Unschedule(StepTimeoutSchedule)
                .If(_ => true, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.Reconcile))),

            When(StepTimeoutSchedule.Received)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.Reconcile))),

            Ignore(SuggestionGenerated));

        During(Reconciling,
            Ignore(TicketCreated),
            Ignore(SuggestionGenerated),
            Ignore(SuggestionGenerationFailed),
            Ignore(StepTimeoutSchedule.Received));

        During(Completed,
            Ignore(TicketCreated),
            Ignore(ProposeSuggestion.Completed),
            Ignore(ProposeSuggestion.Faulted),
            Ignore(ProposeSuggestion.TimeoutExpired),
            When(SuggestionGenerated)
                .Then(TicketSuggestionActivities.AuditLateMessageIgnored));

        During(Discarded,
            Ignore(TicketCreated),
            Ignore(ProposeSuggestion.Completed),
            Ignore(ProposeSuggestion.Faulted),
            Ignore(ProposeSuggestion.TimeoutExpired),
            When(SuggestionGenerated)
                .Then(TicketSuggestionActivities.AuditLateMessageIgnored));

        During(Failed,
            Ignore(TicketCreated),
            Ignore(ProposeSuggestion.Completed),
            Ignore(ProposeSuggestion.Faulted),
            Ignore(ProposeSuggestion.TimeoutExpired),
            When(SuggestionGenerated)
                .Then(TicketSuggestionActivities.AuditLateMessageIgnored));
    }

    private EventActivityBinder<TicketSuggestionSaga, T> AfterReconcile<T>(
        EventActivityBinder<TicketSuggestionSaga, T> binder)
        where T : class =>
        binder
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Propose, propose => propose
                .Request(ProposeSuggestion, ctx => TicketSuggestionActivities.CreateProposeRequest(ctx))
                .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                .TransitionTo(ApplyingSuggestion))
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Retry, retry => retry
                .Send(GenerateSuggestionEndpoint, ctx => TicketSuggestionActivities.CreateGenerateRequest(ctx))
                .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                .TransitionTo(GeneratingSuggestion))
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Fail, fail => fail
                .Publish(ctx => new AutoSuggestionFailed(ctx.Saga.JobId, ctx.Saga.TicketId, ctx.Saga.FailureReason ?? "Failed"))
                .TransitionTo(Failed));
}

public sealed class TicketSuggestionStateMachineDefinition : SagaDefinition<TicketSuggestionSaga>
{
    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<TicketSuggestionSaga> sagaConfigurator,
        IRegistrationContext context)
    {
        if (endpointConfigurator is IServiceBusReceiveEndpointConfigurator sb)
        {
            sb.PrefetchCount = 0;
            sb.LockDuration = TimeSpan.FromMinutes(5);
            sb.MaxAutoRenewDuration = TimeSpan.FromMinutes(15);
            sb.MaxDeliveryCount = 5;
            sb.ConfigureDeadLetterQueueErrorTransport();
            sb.ConfigureDeadLetterQueueDeadLetterTransport();
        }

        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 2000, 5000));
        endpointConfigurator.ConcurrentMessageLimit = 1;
        endpointConfigurator.UseEntityFrameworkOutbox<OrchestratorDbContext>(context);
    }
}
