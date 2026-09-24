using ConexyAI.Entity;

namespace ConexyAI.Repository;

// RAG: добавлено 2026-09-17
public interface IDocumentRepository
{
    Task<Document> AddDocumentAsync(Document document, CancellationToken ct = default);
    Task<Document?> GetDocumentAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListDocumentsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentSearchRow>> SearchAsync(
        Guid userId, string query, int limit, string? documentName, string textSearchConfig, CancellationToken ct = default);
    Task<DocumentChunk?> GetChunkAsync(Guid userId, Guid documentId, int? chunkIndex, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentChunk>> GetChunksAsync(Guid userId, Guid documentId, CancellationToken ct = default);

    // RAG_DOCUMENTS: добавлено 2026-09-24 (ревью L6, M9)
    /// <summary>Number of chunks of the user's document (0 for a foreign or unknown id).</summary>
    Task<int> CountChunksAsync(Guid userId, Guid documentId, CancellationToken ct = default);

    /// <summary>Up to <paramref name="take"/> chunks from <paramref name="fromIndex"/> on, in order.</summary>
    Task<IReadOnlyList<DocumentChunk>> GetChunkRangeAsync(Guid userId, Guid documentId, int fromIndex, int take, CancellationToken ct = default);

    /// <summary>Deletes the user's document and its chunks; returns the deleted row (for its file) or null.</summary>
    Task<Document?> DeleteDocumentAsync(Guid userId, Guid id, CancellationToken ct = default);

    /// <summary>Deletes every document of the user with its chunks; returns the deleted rows.</summary>
    Task<IReadOnlyList<Document>> DeleteUserDocumentsAsync(Guid userId, CancellationToken ct = default);
}

// RAG: добавлено 2026-09-17
/// <summary>A ranked full-text search hit (not an EF-mapped entity; returned from raw SQL).</summary>
public class DocumentSearchRow
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string Title { get; set; } = null!;
    public double Score { get; set; }
}
