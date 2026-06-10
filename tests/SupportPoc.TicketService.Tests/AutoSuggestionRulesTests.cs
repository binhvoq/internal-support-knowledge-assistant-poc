using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class AutoSuggestionRulesTests
{
    [Fact]
    public void Can_accept_when_new_without_suggestion_or_final_answer()
    {
        var ticket = NewTicket(TicketStatus.New);
        Assert.True(AutoSuggestionRules.CanAccept(ticket));
        Assert.Null(AutoSuggestionRules.GetRejectReason(ticket));
    }

    [Fact]
    public void Rejects_when_resolved()
    {
        var ticket = NewTicket(TicketStatus.Resolved);
        ticket.FinalAnswer = "done";
        Assert.False(AutoSuggestionRules.CanAccept(ticket));
        Assert.NotNull(AutoSuggestionRules.GetRejectReason(ticket));
    }

    [Fact]
    public void Rejects_when_already_has_suggestion()
    {
        var ticket = NewTicket(TicketStatus.Suggested);
        ticket.AiSuggestedAnswer = "existing";
        Assert.False(AutoSuggestionRules.CanAccept(ticket));
    }

    [Fact]
    public void Rejects_when_version_mismatch()
    {
        var ticket = NewTicket(TicketStatus.New);
        ticket.Version = 5;
        Assert.False(AutoSuggestionRules.CanAccept(ticket, expectedVersion: 1));
        Assert.Contains("version mismatch", AutoSuggestionRules.GetRejectReason(ticket, 1), StringComparison.OrdinalIgnoreCase);
    }

    private static TicketEntity NewTicket(string status) => new()
    {
        Id = "TCK-1",
        EmployeeId = "EMP-1",
        Category = SupportCategory.IT,
        Question = "Q",
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
