using MassTransit;
using SupportPoc.AiOrchestrator.Saga.Timeouts.Core;

namespace SupportPoc.AiOrchestrator.Saga;

internal static class SagaTimeoutOutcomeHelper
{
    public static bool Is(BehaviorContext<TicketSuggestionState> context, SagaTimeoutOutcome outcome) =>
        string.Equals(
            context.Saga.PendingTimeoutOutcome,
            outcome.ToString(),
            StringComparison.Ordinal);
}
