namespace SupportPoc.Shared.Messaging;

/// <summary>
/// Optional HTTP dev bridge between TicketService and AiOrchestrator.
/// Best-effort debug shortcut only — does not provide Outbox guarantees.
/// </summary>
public sealed class LocalMessagingOptions
{
    public const string SectionName = "LocalMessaging";
    public const string UseHttpBridgeEnvironmentVariable = "USE_HTTP_BRIDGE";

    /// <summary>
    /// Enable HTTP bridge (ticket-created) instead of cross-process Service Bus.
    /// Default false. Set USE_HTTP_BRIDGE=true for an explicit debug shortcut.
    /// </summary>
    public bool HttpBridgeEnabled { get; set; }

    public string TicketServiceBaseUrl { get; set; } = "http://localhost:5001";

    public string AiOrchestratorBaseUrl { get; set; } = "http://localhost:5003";

    public void ApplyEnvironmentOverrides()
    {
        var raw = Environment.GetEnvironmentVariable(UseHttpBridgeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return;

        HttpBridgeEnabled = raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
