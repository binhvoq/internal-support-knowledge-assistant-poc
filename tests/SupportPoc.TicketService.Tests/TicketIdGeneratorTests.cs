using SupportPoc.Shared;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class TicketIdGeneratorTests
{
    [Fact]
    public void NewId_returns_non_empty_uuidv7_format_n_string()
    {
        var id = TicketIdGenerator.NewId();

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(TicketIds.HexLength, id.Length);
        Assert.True(TicketIds.IsValidFormat(id));
        Assert.True(TicketIdGenerator.IsValidFormat(id));
    }

    [Fact]
    public void NewId_generates_unique_ids()
    {
        const int count = 1_000;
        var ids = Enumerable.Range(0, count).Select(_ => TicketIdGenerator.NewId()).ToArray();

        Assert.Equal(count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NewId_generates_unique_ids_under_parallel_load()
    {
        const int count = 2_000;
        var ids = new string[count];

        Parallel.For(0, count, i => ids[i] = TicketIdGenerator.NewId());

        Assert.Equal(count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.True(TicketIds.IsValidFormat(id)));
    }

    [Fact]
    public void IsValidFormat_rejects_32_hex_that_is_not_uuidv7()
    {
        Assert.False(TicketIds.IsValidFormat("01900000000000000000000000000001"));
        Assert.False(TicketIds.IsValidFormat("01932b5c7f8a4000a1b2c3d4e5f67890"));
        Assert.False(TicketIds.IsValidFormat("ffffffffffffffffffffffffffffffff"));
    }

    [Fact]
    public void TryExtractFromText_finds_embedded_uuidv7_ticket_id()
    {
        var extracted = TicketIds.TryExtractFromText($"Ticket {TicketIds.Example} cua toi xu ly den dau roi?");

        Assert.Equal(TicketIds.Example, extracted);
    }

    [Fact]
    public void TryExtractFromText_ignores_non_uuidv7_32_hex_strings()
    {
        const string legacyLikeId = "01900000000000000000000000000001";

        Assert.Null(TicketIds.TryExtractFromText($"Ticket {legacyLikeId} cua toi xu ly den dau roi?"));
    }

    [Fact]
    public void TestTicketIds_constants_are_valid_uuidv7_format()
    {
        Assert.True(TicketIds.IsValidFormat(TestTicketIds.Default));
        Assert.True(TicketIds.IsValidFormat(TestTicketIds.Second));
        Assert.True(TicketIds.IsValidFormat(TestTicketIds.Missing));
        Assert.True(TicketIds.IsValidFormat(TestTicketIds.Gone));
        Assert.True(TicketIds.IsValidFormat(TestTicketIds.Inbox));
        Assert.True(TicketIds.IsValidFormat(TestTicketIds.Lifecycle));
    }
}
