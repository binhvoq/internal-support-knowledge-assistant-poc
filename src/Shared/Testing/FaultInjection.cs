namespace SupportPoc.Shared.Testing;

/// <summary>Marker trong Question de test failure modes (dev/test only).</summary>
public static class FaultInjection
{
    public const string ForceAiFail = "__FAIL_AI__";

    /// <summary>Throw uncaught trong AI step → MassTransit retry → DLQ.</summary>
    public const string ForcePoisonAi = "__POISON_AI__";

    /// <summary>AI xong nhung khong gui Consider (test job stuck Produced).</summary>
    public const string ForceSkipConsider = "__SKIP_CONSIDER__";

    public static bool Has(this string? text, string marker)
        => !string.IsNullOrEmpty(text) && text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
