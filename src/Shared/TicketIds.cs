using System.Text.RegularExpressions;

namespace SupportPoc.Shared;

public static partial class TicketIds
{
    public const int HexLength = 32;

    /// <summary>UUIDv7 format N example for API/MCP descriptions (not a live ticket).</summary>
    public const string Example = "01932b5c7f8a7f8a81b2c3d4e5f67890";

    private const string UuidV7FormatNPattern = @"^[0-9a-f]{12}7[0-9a-f]{3}[89ab][0-9a-f]{15}$";
    private const string EmbeddedUuidV7FormatNPattern = @"\b[0-9a-f]{12}7[0-9a-f]{3}[89ab][0-9a-f]{15}\b";
    private const string EmbeddedHumanTicketPattern = @"\bTK-\d{4}-\d{3,}\b";

    [GeneratedRegex(UuidV7FormatNPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UuidV7Pattern();

    [GeneratedRegex(EmbeddedUuidV7FormatNPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUuidV7Pattern();

    [GeneratedRegex(EmbeddedHumanTicketPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedHumanTicketPatternRegex();

    public static bool IsValidFormat(string? id) =>
        !string.IsNullOrEmpty(id) && UuidV7Pattern().IsMatch(id);

    public static string? TryExtractFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = EmbeddedUuidV7Pattern().Match(text);
        if (match.Success)
            return match.Value.ToLowerInvariant();

        match = EmbeddedHumanTicketPatternRegex().Match(text);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }
}
