using MassTransit;
using SupportPoc.AiOrchestrator.Data;

namespace SupportPoc.AiOrchestrator.Saga;

// SagaDefinition cho phep cau hinh:
// - Endpoint name (queue receive)
// - Retry policy (exponential / immediate / interval)
// - Transactional outbox tren receive endpoint (vua Inbox dedupe, vua Outbox cho publish trong saga)
// - Concurrency limit
public sealed class TicketSuggestionStateDefinition : SagaDefinition<TicketSuggestionState>
{
    public TicketSuggestionStateDefinition()
    {
        // Ten queue ma MassTransit subscribe events ve.
        EndpointName = "ticket-suggestion-saga";

        // Tranh contention - moi instance saga xu ly tuan tu trong process.
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<TicketSuggestionState> sagaConfigurator,
        IRegistrationContext context)
    {
        // Retry voi backoff - khac han try/catch handcoded cu.
        // - Lan 1: 100ms
        // - Lan 2: 500ms
        // - Lan 3: 1s
        // - Lan 4-5: 2s
        // Sau do message vao _error queue (Service Bus DLQ).
        endpointConfigurator.UseMessageRetry(r =>
            r.Intervals(100, 500, 1000, 2000, 2000));

        // Redelivery cho transient error keo dai (vd. DB tam thoi mat ket noi).
        endpointConfigurator.UseDelayedRedelivery(r =>
            r.Intervals(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1)));

        // Transactional outbox + inbox tren chinh endpoint cua saga.
        // - Inbox: dedupe theo MessageId, fix step-level idempotency.
        // - Outbox: moi message saga Send/Publish duoc ghi vao OutboxMessage table
        //   cung transaction voi saga state -> giai quyet dual-write problem.
        endpointConfigurator.UseEntityFrameworkOutbox<OrchestratorDbContext>(context);

        // ---------------------------------------------------------------------
        // TRADE-OFF: Service Bus Sessions (message ordering per CorrelationId)
        // ---------------------------------------------------------------------
        // Sessions dam bao TAT CA message cua mot saga vao 1 partition rieng,
        // xu ly tuan tu -> hard ordering. Hien tai khong bat vi:
        //
        // 1) Optimistic concurrency (ISagaVersion + Version IsConcurrencyToken)
        //    da xu ly race condition: neu 2 message ve cung saga arrive cung luc,
        //    SaveChanges fail -> MassTransit retry -> message tiep theo se thay
        //    state moi nhat. Khong sai logic.
        // 2) Bat sessions yeu cau:
        //    - Queue phai duoc create voi RequiresSession=true (xoa va re-create).
        //    - Moi outgoing message phai set SessionId = CorrelationId.ToString().
        //    - Disable parallel processing trong 1 session -> giam throughput.
        //
        // Khi nao nen bat: neu downstream/UI yeu cau ordering tuyet doi (ex:
        // event stream replay phai theo dung trinh tu cu the), hoac concurrency
        // conflict gay no luc retry qua nhieu.
        //
        // Cach bat (cho doi sau):
        //   if (endpointConfigurator is IServiceBusReceiveEndpointConfigurator sb)
        //       sb.RequiresSession = true;
        //   // Va: cau hinh SessionIdProvider tren AddMassTransit() de set SessionId.
        // ---------------------------------------------------------------------
    }
}
