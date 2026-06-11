using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class TicketIdGeneratorTests
{
    [Fact]
    public void Next_returns_TCK_001_when_empty()
    {
        Assert.Equal("TCK-001", TicketIdGenerator.Next([]));
    }

    [Fact]
    public void Next_increments_from_highest_existing_id()
    {
        var ids = new[] { "TCK-001", "TCK-010", "TCK-003" };
        Assert.Equal("TCK-011", TicketIdGenerator.Next(ids));
    }
}
