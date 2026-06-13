using SupportPoc.Shared;

namespace SupportPoc.TicketService.Services;

internal static class TicketIdGenerator
{
    public static string NewId() => Guid.CreateVersion7().ToString("N");

    public static bool IsValidFormat(string? id) => TicketIds.IsValidFormat(id);
}
