using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

/// <summary>
/// Quy tắc phản hồi rõ ràng cho saga command — tránh im lặng khi lệnh không còn hợp lệ.
/// </summary>
internal static class SagaCommandFeedback
{
    /// <summary>Mark đã apply nhưng event <see cref="ITicketAnalyzingMarked"/> bị mất.</summary>
    public static bool TryGetMarkAlreadyAppliedEpoch(
        TicketEntity ticket,
        IMarkTicketAnalyzing msg,
        out int markedEpoch)
    {
        markedEpoch = 0;

        if (ticket.ActiveSagaCorrelationId != msg.CorrelationId)
            return false;

        if (ticket.Status != TicketStatus.Analyzing)
            return false;

        if (ticket.SagaEpoch != msg.ExpectedEpoch + 1)
            return false;

        markedEpoch = ticket.SagaEpoch;
        return true;
    }

    /// <summary>Save đã apply nhưng event <see cref="ITicketSuggestionSaved"/> bị mất.</summary>
    public static bool IsSaveAlreadyApplied(TicketEntity ticket, ISaveTicketSuggestion msg) =>
        ticket.Status == TicketStatus.Suggested
        && !string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer)
        && ticket.SagaEpoch == msg.ExpectedEpoch
        && (ticket.ActiveSagaCorrelationId is null || ticket.ActiveSagaCorrelationId == msg.CorrelationId);

    public static string StaleMarkReason(TicketEntity ticket, IMarkTicketAnalyzing msg) =>
        $"Stale command: ticket epoch={ticket.SagaEpoch}, expected={msg.ExpectedEpoch}, activeSaga={ticket.ActiveSagaCorrelationId}";

    public static string StaleSaveReason(TicketEntity ticket, ISaveTicketSuggestion msg) =>
        $"Stale command: ticket epoch={ticket.SagaEpoch}, expected={msg.ExpectedEpoch}, activeSaga={ticket.ActiveSagaCorrelationId}";

    public static string ConcurrencyConflictReason(string step) =>
        $"Concurrency conflict: {step} no longer valid at TicketService";

    /// <summary>Agent hoặc saga khác đã đưa ticket sang trạng thái terminal — compensate không mutate.</summary>
    public static bool IsSupersededForCompensate(TicketEntity ticket, ICompensateMarkAnalyzing msg) =>
        ticket.ActiveSagaCorrelationId != msg.CorrelationId
        && ticket.Status is TicketStatus.Resolved or TicketStatus.Suggested;
}
