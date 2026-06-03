namespace SupportPoc.AiOrchestrator.Mcp;

/// <summary>Metadata cho audit và tracing khi gọi MCP qua gateway.</summary>
public sealed record McpCallContext(
    string Source,
    Guid? SagaCorrelationId = null,
    string? TicketId = null)
{
    public const string SourceChat = "chat";
    public const string SourceOfflineChat = "offline_chat";
    public const string SourceDirect = "direct";
}
