namespace SupportPoc.Shared.Options;

public sealed class AzureAdOptions
{
    public const string SectionName = "AzureAd";

    public bool Enabled { get; set; }
    public string TenantId { get; set; } = "";
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string? Authority { get; set; }
    /// <summary>API app (resource server) client id — JWT audience validation.</summary>
    public string ClientId { get; set; } = "";
    public string Audience { get; set; } = "";
    public string? Scope { get; set; }
    /// <summary>MCP service app — client credentials.</summary>
    public string? McpClientId { get; set; }
    public string? McpClientSecret { get; set; }
    /// <summary>Alias for McpClientSecret on McpToolServer.</summary>
    public string? ClientSecret { get; set; }
}
