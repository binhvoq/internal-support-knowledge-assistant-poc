using SupportPoc.Shared.Auth;

namespace SupportPoc.McpToolServer.Tools;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SupportToolPolicyAttribute : Attribute
{
    public SupportToolPolicyAttribute(string risk, params string[] roles)
    {
        Risk = risk;
        Roles = roles;
    }

    public IReadOnlyList<string> Roles { get; }
    public string Risk { get; }
    public string Notes { get; init; } = "";
}

public static class SupportToolRisks
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}
