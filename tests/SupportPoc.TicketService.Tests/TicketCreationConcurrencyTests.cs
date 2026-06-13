using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class TicketCreationConcurrencyTests : IDisposable
{
    private readonly DbContextOptions<TicketDbContext> _options;
    private readonly TicketDbContext _db;

    public TicketCreationConcurrencyTests()
    {
        _options = new DbContextOptionsBuilder<TicketDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new TicketDbContext(_options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Parallel_NewId_generation_produces_unique_persistable_ticket_ids()
    {
        const int count = 500;
        var ids = new string[count];
        Parallel.For(0, count, i => ids[i] = TicketIdGenerator.NewId());

        Assert.Equal(count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.True(TicketIds.IsValidFormat(id)));

        var now = DateTimeOffset.UtcNow;
        foreach (var id in ids)
        {
            _db.Tickets.Add(new TicketEntity
            {
                Id = id,
                EmployeeId = "emp@test",
                Category = SupportCategory.Other,
                Question = "question?",
                Status = TicketStatus.New,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await _db.SaveChangesAsync();
        Assert.Equal(count, await _db.Tickets.CountAsync());
    }

    public void Dispose() => _db.Dispose();
}
