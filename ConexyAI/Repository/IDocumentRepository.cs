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
