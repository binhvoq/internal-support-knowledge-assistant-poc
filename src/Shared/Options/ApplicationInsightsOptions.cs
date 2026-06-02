namespace SupportPoc.Shared.Options;

public sealed class ApplicationInsightsOptions
{
    public const string SectionName = "ApplicationInsights";

    public string? ConnectionString { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
}
