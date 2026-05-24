namespace SupportPoc.TicketService.Services;

internal static class TicketIdGenerator
{
    public static string Next(IEnumerable<string> existingIds)
    {
        var max = existingIds
            .Select(id => int.TryParse(id.Replace("TCK-", "", StringComparison.OrdinalIgnoreCase), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"TCK-{(max + 1):D3}";
    }
}
