namespace SupportPoc.Shared.Messaging;

/// <summary>
/// Local service base URLs for cross-service HTTP calls (e.g. ticket snapshot client).
/// </summary>
public sealed class LocalMessagingOptions
{
    public const string SectionName = "LocalMessaging";

    public string TicketServiceBaseUrl { get; set; } = "http://localhost:5001";
}
