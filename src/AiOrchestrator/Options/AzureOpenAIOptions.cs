namespace SupportPoc.AiOrchestrator.Options;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? ChatEndpoint { get; set; }
    public string? ChatApiKey { get; set; }
    public string ChatDeployment { get; set; } = "gpt-4.1-mini";
    /// <summary>Neu false: Semantic Kernel chat dung MCP fallback; pipeline dung direct knowledge search + offline generation.</summary>
    public bool? ChatEnabled { get; set; }
    public string ChatEndpointResolved => ChatEndpoint ?? Endpoint ?? "";
    public string ChatApiKeyResolved => ChatApiKey ?? ApiKey ?? "";
    public bool ChatConfigured =>
        !string.IsNullOrWhiteSpace(ChatEndpointResolved) && !string.IsNullOrWhiteSpace(ChatApiKeyResolved);
    public bool ChatEnabledResolved => ChatEnabled ?? ChatConfigured;
    /// <summary>Alias — giu tuong thich voi code cu.</summary>
    public bool Enabled => ChatEnabledResolved;
}
