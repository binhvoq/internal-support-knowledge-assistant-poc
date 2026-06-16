using MassTransit;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Telemetry;

namespace SupportPoc.AiOrchestrator.Saga;

public sealed class TicketSuggestionStateMachine : MassTransitStateMachine<TicketSuggestionSaga>
{
    private static readonly Uri GenerateSuggestionEndpoint = new("queue:generate-suggestion-requested");

    public State GeneratingSuggestion { get; private set; } = null!;
    public State ApplyingSuggestion { get; private set; } = null!;
    public State Reconciling { get; private set; } = null!;
    public State ReconcileUnknown { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Discarded { get; private set; } = null!;
    public State Failed { get; private set; } = null!;

    public Event<ITicketCreated> TicketCreated { get; private set; } = null!;
    public Event<ISuggestionGenerated> SuggestionGenerated { get; private set; } = null!;
    public Event<ISuggestionGenerationFailed> SuggestionGenerationFailed { get; private set; } = null!;
    public Event<IReconcileSweep> ReconcileSweep { get; private set; } = null!;
    public Event<IReconcileAbandon> ReconcileAbandon { get; private set; } = null!;
    public Event<IReconcileEscalate> ReconcileEscalate { get; private set; } = null!;
    public Event<IReconcileRedrive> ReconcileRedrive { get; private set; } = null!;
    public Event<IStuckStepSweep> StuckStepSweep { get; private set; } = null!;

    public Schedule<TicketSuggestionSaga, IStepTimeout> StepTimeoutSchedule { get; private set; } = null!;
    public Schedule<TicketSuggestionSaga, IGenerationCheck> GenerationCheckSchedule { get; private set; } = null!;

    public Request<TicketSuggestionSaga, IProposeTicketSuggestion, IProposeTicketSuggestionResult> ProposeSuggestion { get; private set; } = null!;

    public TicketSuggestionStateMachine(IOptions<AutoSuggestionOptions> options)
    {
        var opts = options.Value;
        var stepTimeout = TimeSpan.FromSeconds(Math.Max(5, opts.StepTimeoutSeconds));
        var generationCheckInterval = TimeSpan.FromSeconds(Math.Max(5, opts.GenerationCheckIntervalSeconds));
        var proposeTimeout = TimeSpan.FromSeconds(Math.Max(5, opts.ProposeRequestTimeoutSeconds));

        InstanceState(x => x.CurrentState);

        Event(() => TicketCreated, e =>
        {
            e.CorrelateById(m => m.Message.JobId);
            e.InsertOnInitial = true;
            e.SetSagaFactory(ctx => new TicketSuggestionSaga
            {
                CorrelationId = ctx.Message.JobId
            });
        });
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
        Event(() => ReconcileSweep, e => e.CorrelateById(m => m.Message.SagaId));
        Event(() => ReconcileAbandon, e => e.CorrelateById(m => m.Message.SagaId));
        Event(() => ReconcileEscalate, e => e.CorrelateById(m => m.Message.SagaId));
        Event(() => ReconcileRedrive, e => e.CorrelateById(m => m.Message.SagaId));
        Event(() => StuckStepSweep, e => e.CorrelateById(m => m.Message.SagaId));

        Schedule(() => StepTimeoutSchedule, x => x.StepTimeoutTokenId, s =>
        {
            s.Delay = stepTimeout;
            s.Received = r => r.CorrelateById(m => m.Message.SagaId);
        });

        Schedule(() => GenerationCheckSchedule, x => x.GenerationCheckTokenId, s =>
        {
            s.Delay = generationCheckInterval;
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
                    .Unschedule(GenerationCheckSchedule)
                    .Request(ProposeSuggestion, ctx => TicketSuggestionActivities.CreateProposeRequest(ctx))
                    .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                    .TransitionTo(ApplyingSuggestion))
                .If(ctx => ctx.Message.AttemptId != ctx.Saga.CurrentAttemptId, then => then
                    .Then(TicketSuggestionActivities.AuditLateAttempt)),

            When(SuggestionGenerationFailed)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.Unschedule(StepTimeoutSchedule)
                        .Unschedule(GenerationCheckSchedule)
                        .TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.BeginReconciling)
                        .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>()))),

            When(StepTimeoutSchedule.Received)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.BeginReconciling)
                        .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>()))),

            When(GenerationCheckSchedule.Received)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.Unschedule(GenerationCheckSchedule)
                        .TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.BeginReconciling)
                        .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>()))),

            AfterReconcile(
                When(StuckStepSweep)
                    .Unschedule(StepTimeoutSchedule)
                    .Unschedule(GenerationCheckSchedule)
                    .TransitionTo(Reconciling)
                    .Then(TicketSuggestionActivities.BeginReconciling)
                    .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>())));

        During(ApplyingSuggestion,
            Ignore(TicketCreated),
            When(ProposeSuggestion.Completed)
                .Unschedule(StepTimeoutSchedule)
                .If(ctx => ctx.Message.Accepted, accepted => accepted
                    .Publish(ctx => new AiSuggestionGenerated(ctx.Saga.JobId, ctx.Saga.TicketId))
                    .TransitionTo(Completed))
                .If(ctx => !ctx.Message.Accepted, rejected => rejected
                    .Then(ctx =>
                    {
                        ctx.Saga.DiscardReason = ctx.Message.Reason;
                        ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    })
                    .Publish(ctx => new AutoSuggestionDiscarded(
                        ctx.Saga.JobId,
                        ctx.Saga.TicketId,
                        ctx.Saga.DiscardReason ?? "Rejected"))
                    .TransitionTo(Discarded)),

            When(ProposeSuggestion.Faulted)
                .Unschedule(StepTimeoutSchedule)
                .If(_ => true, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.BeginReconciling)
                        .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>()))),

            When(ProposeSuggestion.TimeoutExpired)
                .Unschedule(StepTimeoutSchedule)
                .If(_ => true, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.BeginReconciling)
                        .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>()))),

            When(StepTimeoutSchedule.Received)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.TransitionTo(Reconciling)
                        .Then(TicketSuggestionActivities.BeginReconciling)
                        .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>()))),

            AfterReconcile(
                When(StuckStepSweep)
                    .Unschedule(StepTimeoutSchedule)
                    .TransitionTo(Reconciling)
                    .Then(TicketSuggestionActivities.BeginReconciling)
                    .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>())),

            Ignore(SuggestionGenerated));

        During(Reconciling,
            Ignore(TicketCreated),
            When(SuggestionGenerated)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => then
                    .Then(TicketSuggestionActivities.StoreGeneratedResult)
                    .Unschedule(StepTimeoutSchedule)
                    .Unschedule(GenerationCheckSchedule)
                    .Request(ProposeSuggestion, ctx => TicketSuggestionActivities.CreateProposeRequest(ctx))
                    .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                    .TransitionTo(ApplyingSuggestion))
                .If(ctx => ctx.Message.AttemptId != ctx.Saga.CurrentAttemptId, then => then
                    .Then(TicketSuggestionActivities.AuditLateAttempt)),
            When(SuggestionGenerationFailed)
                .If(ctx => ctx.Message.AttemptId == ctx.Saga.CurrentAttemptId, then => AfterReconcile(
                    then.Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>()))),
            Ignore(StepTimeoutSchedule.Received),
            Ignore(GenerationCheckSchedule.Received),
            When(ProposeSuggestion.Completed)
                .Unschedule(StepTimeoutSchedule)
                .If(ctx => ctx.Message.Accepted, accepted => accepted
                    .Publish(ctx => new AiSuggestionGenerated(ctx.Saga.JobId, ctx.Saga.TicketId))
                    .TransitionTo(Completed))
                .If(ctx => !ctx.Message.Accepted, rejected => rejected
                    .Then(ctx =>
                    {
                        ctx.Saga.DiscardReason = ctx.Message.Reason;
                        ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    })
                    .Publish(ctx => new AutoSuggestionDiscarded(
                        ctx.Saga.JobId,
                        ctx.Saga.TicketId,
                        ctx.Saga.DiscardReason ?? "Rejected"))
                    .TransitionTo(Discarded)),
            AfterReconcile(
                When(ReconcileSweep)
                    .Activity(x => x.OfInstanceType<ReconcileTicketSuggestionActivity>())),
            When(ReconcileAbandon)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.Reason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Publish(ctx => new AutoSuggestionFailed(ctx.Saga.JobId, ctx.Saga.TicketId, ctx.Saga.FailureReason ?? "Abandoned"))
                .TransitionTo(Failed),
            When(ReconcileEscalate)
                .ThenAsync(async ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.Reason;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    var queue = ctx.GetServiceOrCreateInstance<ISagaReconciliationQueue>();
                    await queue.UpsertOnEscalateAsync(ctx.Saga, ctx.Message.Reason, ctx.CancellationToken);
                    SagaReconcileTelemetry.TrackEscalatedToUnknown(
                        ctx.GetServiceOrCreateInstance<IServiceProvider>().TryGetTelemetryClient(),
                        ctx.Saga.CorrelationId,
                        ctx.Saga.TicketId,
                        ctx.Message.Reason);
                })
                .Publish(ctx => new AutoSuggestionReconcileUnknown(
                    ctx.Saga.JobId,
                    ctx.Saga.TicketId,
                    ctx.Saga.FailureReason ?? "Reconcile unknown"))
                .TransitionTo(ReconcileUnknown));

        During(ReconcileUnknown,
            Ignore(TicketCreated),
            Ignore(ProposeSuggestion.Completed),
            Ignore(ProposeSuggestion.Faulted),
            Ignore(ProposeSuggestion.TimeoutExpired),
            Ignore(ReconcileSweep),
            Ignore(ReconcileAbandon),
            Ignore(ReconcileEscalate),
            AfterReconcile(
                When(ReconcileRedrive)
                    .Activity(x => x.OfInstanceType<ReconcileUnknownRedriveActivity>())),
            When(SuggestionGenerated)
                .Then(TicketSuggestionActivities.AuditLateMessageIgnored));

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
        ApplyPendingReconcileAction(binder);

    private EventActivityBinder<TicketSuggestionSaga, T> ApplyPendingReconcileAction<T>(
        EventActivityBinder<TicketSuggestionSaga, T> binder)
        where T : class =>
        binder
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Complete, complete => complete
                .Publish(ctx => new AiSuggestionGenerated(ctx.Saga.JobId, ctx.Saga.TicketId))
                .TransitionTo(Completed))
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Discard, discard => discard
                .Publish(ctx => new AutoSuggestionDiscarded(
                    ctx.Saga.JobId,
                    ctx.Saga.TicketId,
                    ctx.Saga.DiscardReason ?? "Discarded"))
                .TransitionTo(Discarded))
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Propose, propose => propose
                .Unschedule(GenerationCheckSchedule)
                .Request(ProposeSuggestion, ctx => TicketSuggestionActivities.CreateProposeRequest(ctx))
                .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                .TransitionTo(ApplyingSuggestion))
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Retry, retry => retry
                .Unschedule(GenerationCheckSchedule)
                .Activity(x => x.OfInstanceType<PrepareGenerationRetryActivity>())
                .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.Retry, prepared => prepared
                    .Send(GenerateSuggestionEndpoint, ctx => TicketSuggestionActivities.CreateGenerateRequest(ctx))
                    .Schedule(StepTimeoutSchedule, ctx => TicketSuggestionActivities.CreateStepTimeout(ctx))
                    .TransitionTo(GeneratingSuggestion))
                .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.WaitForGeneration, deferred => deferred
                    .Schedule(GenerationCheckSchedule, ctx => TicketSuggestionActivities.CreateGenerationCheck(ctx))
                    .TransitionTo(GeneratingSuggestion)))
            .If(ctx => ctx.Saga.PendingReconcileAction == ReconcileActions.WaitForGeneration, wait => wait
                .Schedule(GenerationCheckSchedule, ctx => TicketSuggestionActivities.CreateGenerationCheck(ctx))
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
        // Consumer outbox: Program.cs AddEntityFrameworkConsumerOutbox<OrchestratorDbContext>()
    }
}
