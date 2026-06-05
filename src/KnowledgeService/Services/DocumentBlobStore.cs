using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

    public async Task<string?> UploadKnowledgeFileAsync(
        string documentId,
        Stream stream,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return null;

        try
        {
            var container = await GetContainerAsync(cancellationToken);
            var blobName = BuildBlobName(documentId, fileName);
            var blob = container.GetBlobClient(blobName);
            var blobMetadata = metadata.ToDictionary(
                static pair => SanitizeMetadataKey(pair.Key),
                static pair => TruncateMetadataValue(pair.Key, pair.Value),
                StringComparer.OrdinalIgnoreCase);

            await blob.UploadAsync(
                stream,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                    Metadata = blobMetadata
                },
                cancellationToken);

            return blob.Uri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong upload duoc blob cho {DocumentId}.", documentId);
            throw;
        }
    }

    public async Task DeleteAsync(string documentId, string? fileName, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        try
        {
            var container = await GetContainerAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                await container.GetBlobClient(BuildBlobName(documentId, fileName))
                    .DeleteIfExistsAsync(cancellationToken: cancellationToken);
                return;
            }

            await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{documentId}/", cancellationToken))
            {
                await container.GetBlobClient(item.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong xoa duoc blob cho {DocumentId}.", documentId);
        }
    }

    public static string BuildBlobName(string documentId, string fileName) =>
        $"{documentId}/{fileName}";

    private static string SanitizeMetadataKey(string key) =>
        new(key.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());

    private static string TruncateMetadataValue(string key, string value)
    {
        var max = key.Equals("title", StringComparison.OrdinalIgnoreCase) ? 512 : 256;
        return value.Length <= max ? value : value[..max];
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
