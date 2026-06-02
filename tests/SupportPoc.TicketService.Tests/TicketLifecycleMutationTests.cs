using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class TicketLifecycleMutationTests
{
    [Fact]
    public void TryMutateStatus_with_active_saga_clears_ownership_and_bumps_epoch()
    {
        var sagaId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var ticket = CreateTicket(TicketStatus.Analyzing, sagaId, sagaEpoch: 1);

        var ok = TicketLifecycleMutation.TryMutateStatus(ticket, TicketStatus.Suggested, null, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(TicketStatus.Suggested, ticket.Status);
        Assert.Null(ticket.ActiveSagaCorrelationId);
        Assert.Equal(2, ticket.SagaEpoch);
    }

    [Fact]
    public void TryMutateStatus_resolved_requires_final_answer()
    {
        var ticket = CreateTicket(TicketStatus.Suggested, activeSaga: null, sagaEpoch: 2);

        var ok = TicketLifecycleMutation.TryMutateStatus(ticket, TicketStatus.Resolved, finalAnswer: null, out var error);

        Assert.False(ok);
        Assert.Contains("finalAnswer", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TicketStatus.Suggested, ticket.Status);
    }

    [Fact]
    public void TryMutateStatus_resolved_sets_final_answer()
    {
        var ticket = CreateTicket(TicketStatus.Suggested, activeSaga: null, sagaEpoch: 2);

        var ok = TicketLifecycleMutation.TryMutateStatus(ticket, TicketStatus.Resolved, "Done", out _);

        Assert.True(ok);
        Assert.Equal(TicketStatus.Resolved, ticket.Status);
        Assert.Equal("Done", ticket.FinalAnswer);
    }

    [Fact]
    public void TryMutateStatus_rejects_unknown_status()
    {
        var ticket = CreateTicket(TicketStatus.New, activeSaga: null, sagaEpoch: 0);

        var ok = TicketLifecycleMutation.TryMutateStatus(ticket, "NeedsManualReview", null, out var error);

        Assert.False(ok);
        Assert.Contains("khong hop le", error, StringComparison.OrdinalIgnoreCase);
    }

    private static TicketEntity CreateTicket(string status, Guid? activeSaga, int sagaEpoch) =>
        new()
        {
            Id = "TCK-MUT",
            EmployeeId = "EMP-1",
            Category = SupportCategory.IT,
            Question = "VPN",
            Status = status,
            ActiveSagaCorrelationId = activeSaga,
            SagaEpoch = sagaEpoch,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
