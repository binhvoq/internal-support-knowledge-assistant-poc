namespace SupportPoc.Shared.Messaging;

/// <summary>
/// Dev fallback khi Service Bus khong dung duoc: HTTP bridge giua TicketService va AiOrchestrator.
/// </summary>
public sealed class LocalMessagingOptions
{
    public const string SectionName = "LocalMessaging";

    /// <summary>Bat HTTP bridge (ticket-created + consider) thay vi cross-process Service Bus.</summary>
    public bool HttpBridgeEnabled { get; set; }

    public string TicketServiceBaseUrl { get; set; } = "http://localhost:5001";

    public string AiOrchestratorBaseUrl { get; set; } = "http://localhost:5003";
}
