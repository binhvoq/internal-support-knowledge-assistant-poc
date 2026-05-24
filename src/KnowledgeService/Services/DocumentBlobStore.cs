using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Options;

namespace SupportPoc.KnowledgeService.Services;

public sealed class DocumentBlobStore
{
    private readonly AzureStorageOptions _options;
    private readonly ILogger<DocumentBlobStore> _logger;
    private BlobContainerClient? _container;

    public DocumentBlobStore(IOptions<AzureStorageOptions> options, ILogger<DocumentBlobStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> UploadAsync(string documentId, string content, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return null;

        try
        {
            var container = await GetContainerAsync(cancellationToken);
            var blob = container.GetBlobClient($"{documentId}.txt");
            await blob.UploadAsync(BinaryData.FromString(content), overwrite: true, cancellationToken);
            return blob.Uri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong upload duoc blob cho {DocumentId}.", documentId);
            return null;
        }
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken cancellationToken)
    {
        if (_container is not null) return _container;

        var service = new BlobServiceClient(_options.ConnectionString);
        _container = service.GetBlobContainerClient(_options.ContainerName);
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        return _container;
    }
}
