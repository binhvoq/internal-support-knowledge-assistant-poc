namespace SupportPoc.Shared.Models;

public static class TicketStatus
{
    public const string New = "New";
    public const string Suggested = "Suggested";
    public const string Resolved = "Resolved";
    public const string Reopened = "Reopened";

    public static readonly string[] All = [New, Suggested, Resolved, Reopened];
}
