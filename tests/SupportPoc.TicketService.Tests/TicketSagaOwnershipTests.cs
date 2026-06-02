using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class TicketSagaOwnershipTests
{
    [Fact]
    public void ApplyAgentLifecycleOverride_clears_active_saga_and_bumps_epoch()
    {
        var sagaId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var ticket = new TicketEntity
        {
            Id = "TCK-001",
            EmployeeId = "EMP-1",
            Category = SupportCategory.IT,
            Question = "VPN",
            Status = TicketStatus.Analyzing,
            SagaEpoch = 1,
            ActiveSagaCorrelationId = sagaId,
            AiDraftSuggestion = "draft",
            AiDraftCorrelationId = sagaId,
            AiDraftSagaEpoch = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var applied = TicketSagaOwnership.ApplyAgentLifecycleOverride(ticket);

        Assert.True(applied);
        Assert.Null(ticket.ActiveSagaCorrelationId);
        Assert.Equal(2, ticket.SagaEpoch);
        Assert.Null(ticket.AiDraftSuggestion);
        Assert.Null(ticket.AiDraftCorrelationId);
    }

    [Fact]
    public void ApplyAgentLifecycleOverride_noop_when_no_active_saga()
    {
        var ticket = new TicketEntity
        {
            Id = "TCK-002",
            EmployeeId = "EMP-1",
            Category = SupportCategory.IT,
            Question = "VPN",
            Status = TicketStatus.Suggested,
            SagaEpoch = 3,
            ActiveSagaCorrelationId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var applied = TicketSagaOwnership.ApplyAgentLifecycleOverride(ticket);

        Assert.False(applied);
        Assert.Equal(3, ticket.SagaEpoch);
    }
}
