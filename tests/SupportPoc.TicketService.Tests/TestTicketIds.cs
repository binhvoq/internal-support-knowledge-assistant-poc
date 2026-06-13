namespace SupportPoc.TicketService.Tests;

/// <summary>Deterministic UUIDv7 format N ticket IDs for tests (not production generator output).</summary>
internal static class TestTicketIds
{
    public const string Default = "01900000000070008000000000000001";
    public const string Second = "01900000000070019000000000000002";
    public const string Missing = "0190000000007099a000000000000099";
    public const string Gone = "0190000000007098b000000000000098";
    public const string Inbox = "01900000000070978000000000000097";
    public const string Lifecycle = "01900000000070969000000000000096";
}
