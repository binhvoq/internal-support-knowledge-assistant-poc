namespace SupportPoc.AiOrchestrator.Options;

public sealed class ServiceEndpointsOptions
{
    public const string SectionName = "Services";
    public string TicketService { get; set; } = "http://localhost:5001";
    public string KnowledgeService { get; set; } = "http://localhost:5002";
    public string McpToolServer { get; set; } = "http://localhost:5004";
}
