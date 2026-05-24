namespace SupportPoc.AiOrchestrator.Options;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? ChatEndpoint { get; set; }
    public string? ChatApiKey { get; set; }
    public string ChatDeployment { get; set; } = "gpt-4.1-mini";
    public string ChatEndpointResolved => ChatEndpoint ?? Endpoint ?? "";
    public string ChatApiKeyResolved => ChatApiKey ?? ApiKey ?? "";
    public bool Enabled => !string.IsNullOrWhiteSpace(ChatEndpointResolved) && !string.IsNullOrWhiteSpace(ChatApiKeyResolved);
}
