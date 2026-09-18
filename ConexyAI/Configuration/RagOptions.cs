namespace ConexyAI.Configuration;

// RAG: добавлено 2026-09-17
/// <summary>Binds the <c>Rag</c> section of appsettings.json for the document indexing pipeline.</summary>
public class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Target chunk size in characters (600-1000).</summary>
    public int ChunkSize { get; set; } = 800;

    /// <summary>Overlap between adjacent chunks in characters (100-150).</summary>
    public int ChunkOverlap { get; set; } = 120;

    /// <summary>PostgreSQL full-text search config (default 'russian'; built-in in Postgres).</summary>
    public string TextSearchConfig { get; set; } = "russian";

    /// <summary>Directory where uploaded source documents are stored for (re)indexing.</summary>
    public string DocumentsDirectory { get; set; } = string.Empty;
}
