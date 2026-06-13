using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

internal static class ReconcileUnknownRedrivePolicy
{
    internal static bool IsEligible(TicketSuggestionSaga? saga) =>
        saga is not null && saga.CurrentState == SagaProcessState.ReconcileUnknown;
}
