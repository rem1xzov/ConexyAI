namespace ConexyAI.Entity;

// RAG: добавлено 2026-09-17
/// <summary>An uploaded/indexed document in the RAG knowledge base.</summary>
public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning user. Every query MUST be scoped to this id to prevent cross-user document access.</summary>
    public Guid UserId { get; set; }

    public string Title { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Original location of the source file (for reference/re-indexing).</summary>
    public string FilePath { get; set; } = null!;

    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
}

// RAG: добавлено 2026-09-17
/// <summary>
/// A single text chunk of a <see cref="Document"/>. Search uses PostgreSQL full-text search
/// over <see cref="Content"/> (<c>to_tsvector</c>/<c>websearch_to_tsquery</c>) as the
/// reliable fallback when embeddings are not configured. A dedicated <c>tsvector</c> /
/// pgvector column can be added later for performance or semantic search.
/// </summary>
public class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public string Content { get; set; } = null!;
    public int TokensCount { get; set; }

    public Document Document { get; set; } = null!;
}
