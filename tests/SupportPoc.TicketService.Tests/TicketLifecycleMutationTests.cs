using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class TicketLifecycleMutationTests
{
    [Fact]
    public void Resolve_sets_final_answer_without_saga_epoch()
    {
        var ticket = NewTicket(TicketStatus.Suggested);
        ticket.AiSuggestedAnswer = "ai";

        var ok = TicketLifecycleMutation.TryMutateStatus(ticket, TicketStatus.Resolved, "agent final", out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(TicketStatus.Resolved, ticket.Status);
        Assert.Equal("agent final", ticket.FinalAnswer);
    }

    [Fact]
    public void Reopen_clears_final_answer()
    {
        var ticket = NewTicket(TicketStatus.Resolved);
        ticket.FinalAnswer = "was resolved";

        Assert.True(TicketLifecycleMutation.TryMutateStatus(ticket, TicketStatus.Reopened, null, out _));
        Assert.Equal(TicketStatus.Reopened, ticket.Status);
        Assert.Null(ticket.FinalAnswer);
    }

    private static TicketEntity NewTicket(string status) => new()
    {
        Id = "TCK-LC",
        EmployeeId = "EMP-1",
        Category = SupportCategory.IT,
        Question = "Q",
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
