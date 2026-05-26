namespace SupportPoc.AiOrchestrator.Options;

// Chi con giu McpToolServer endpoint - cac HTTP endpoint khac (TicketService) khong con dung
// vi da chuyen sang messaging.
public sealed class ServiceEndpointsOptions
{
    public const string SectionName = "Services";
    public string McpToolServer { get; set; } = "http://localhost:5004";
}
