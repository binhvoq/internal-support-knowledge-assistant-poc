using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using SupportPoc.KnowledgeService.Options;

namespace SupportPoc.KnowledgeService.Services;

public sealed class EmbeddingService
{
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(IOptions<AzureOpenAIOptions> options, ILogger<EmbeddingService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float>?> CreateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("Azure OpenAI chua cau hinh — bo qua embedding.");
            return null;
        }

        var client = new AzureOpenAIClient(new Uri(_options.Endpoint!), new AzureKeyCredential(_options.ApiKey!));
        var embeddingClient = client.GetEmbeddingClient(_options.EmbeddingDeployment);
        var response = await embeddingClient.GenerateEmbeddingAsync(text, cancellationToken: cancellationToken);
        return response.Value.ToFloats().ToArray();
    }
}
