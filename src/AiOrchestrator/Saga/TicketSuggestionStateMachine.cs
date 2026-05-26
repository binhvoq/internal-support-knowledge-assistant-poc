using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga;

// State machine = orchestrator cua saga.
// Khac voi handcoded ProcessTicketCreatedAsync cu (procedural, sync, khong rollback),
// state machine nay declarative, event-driven, voi compensation tu dong khi fail.
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

    public Schedule<TicketSuggestionState, ISagaTimeoutExpired> Timeout { get; private set; } = null!;

    public TicketSuggestionStateMachine(IOptions<SagaTimeoutOptions> timeoutOptions)
    {
        // Doc timeout tu config -> co the chinh ngan trong test (vd. 30s) hoac dai trong prod (5+ phut)
        // ma khong phai sua state machine.
        var timeoutDelay = TimeSpan.FromSeconds(Math.Max(5, timeoutOptions.Value.TimeoutSeconds));

        InstanceState(x => x.CurrentState);

        // ---------- EVENT CORRELATION ----------
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

        // ---------- TIMEOUT SCHEDULE ----------
        Schedule(() => Timeout, x => x.TimeoutTokenId, s =>
        {
            s.Delay = timeoutDelay;
            s.Received = r => r.CorrelateById(ctx => ctx.Message.CorrelationId);
        });

        // ---------- TRANSITIONS ----------

        Initially(
            When(TicketCreated)
                .Then(ctx =>
                {
                    ctx.Saga.OriginalStatus = TicketStatus.New;
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Schedule(Timeout, ctx => new SagaTimeoutExpired(ctx.Saga.CorrelationId, ctx.Saga.TicketId))
                .Send(ctx => new MarkTicketAnalyzing(ctx.Saga.CorrelationId, ctx.Saga.TicketId))
                .TransitionTo(Analyzing));

        During(Analyzing,
            When(AnalyzingMarked)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow)
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
                .Unschedule(Timeout)
                // Chua mark thanh cong => khong can compensate. Saga ket thuc o Failed.
                .TransitionTo(Failed),

            When(Timeout.Received)
                .Then(ctx =>
                {
                    // Gui compensate thay vi chuyen Failed cung - phong truong hop
                    // TicketService da PATCH Analyzing nhung event bi mat tren broker.
                    // Compensation idempotent: neu ticket khong o Analyzing -> no-op.
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
                // Da mark analyzing => CAN compensate.
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(Compensating),

            When(Timeout.Received)
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
                .Unschedule(Timeout)
                // Broadcast cho downstream subscribe.
                .PublishAsync(ctx => ctx.Init<IAiSuggestionGenerated>(
                    new AiSuggestionGenerated(ctx.Saga.CorrelationId, ctx.Saga.TicketId)))
                .TransitionTo(Completed),

            When(SuggestionSaveFailed)
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

            When(Timeout.Received)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationReason = "Timeout khi cho SaveSuggestion.";
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                .Send(ctx => new CompensateMarkAnalyzing(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.TicketId,
                    ctx.Saga.OriginalStatus))
                .TransitionTo(Compensating));

        During(Compensating,
            When(MarkAnalyzingReverted)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow)
                .Unschedule(Timeout)
                .TransitionTo(Compensated),

            When(Timeout.Received)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = $"Compensation cung timeout: {ctx.Saga.CompensationReason}";
                    ctx.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                })
                // Hard fail - operator phai vao xem DLQ + saga state.
                .TransitionTo(Failed));

        // Ignore late-arriving events o terminal states (tranh fault khi message delayed).
        DuringAny(
            Ignore(MarkAnalyzingReverted),
            Ignore(AnalyzingMarked),
            Ignore(AnalyzingMarkFailed),
            Ignore(SuggestionSaved),
            Ignore(SuggestionSaveFailed),
            Ignore(AiPipelineCompleted),
            Ignore(AiPipelineFailed),
            Ignore(Timeout.Received));
    }
}
