using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

/// <summary>
/// Luật tranh quyền: agent lifecycle thắng automation đang giữ ticket qua saga.
/// </summary>
public static class TicketSagaOwnership
{
    /// <summary>
    /// Khi agent resolve/reopen (hoặc mutate lifecycle khác), vô hiệu lệnh saga cũ
    /// (save suggestion, mark analyzing, ...) bằng clear ownership + bump epoch.
    /// </summary>
    public static bool ApplyAgentLifecycleOverride(TicketEntity ticket)
    {
        if (ticket.ActiveSagaCorrelationId is null)
            return false;

        ticket.ActiveSagaCorrelationId = null;
        ticket.SagaEpoch++;
        TicketAiDraftHelper.ClearDraft(ticket);
        return true;
    }
}
