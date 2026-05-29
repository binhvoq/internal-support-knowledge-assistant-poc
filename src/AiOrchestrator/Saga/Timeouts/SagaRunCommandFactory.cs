using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts;

public static class SagaRunCommandFactory
{
    public static RunAiPipeline Create(TicketSuggestionState saga) =>
        new(
            saga.CorrelationId,
            saga.TicketId,
            saga.TicketSagaEpoch,
            saga.Question,
            saga.Category ?? SupportCategory.Other);
}
