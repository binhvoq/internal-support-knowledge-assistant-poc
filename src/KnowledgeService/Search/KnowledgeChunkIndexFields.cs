namespace SupportPoc.KnowledgeService.Search;

/// <summary>Azure AI Search field names for the chunk-level knowledge index.</summary>
public static class KnowledgeChunkIndexFields
{
    public const string ChunkId = "chunkId";
    public const string ParentId = "parentId";
    public const string DocumentId = "documentId";
    public const string Title = "title";
    public const string Category = "category";
    public const string FileName = "fileName";
    public const string SourceUrl = "sourceUrl";
    public const string Content = "content";
    public const string UploadedAt = "uploadedAt";
    public const string Embedding = "embedding";
    public const string VectorProfile = "vector-profile";
}
