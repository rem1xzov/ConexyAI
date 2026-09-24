using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

// RAG: добавлено 2026-09-17
/// <summary>Arguments for the <c>search_documents</c> tool.</summary>
public class SearchDocumentsRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }
}

// RAG: добавлено 2026-09-17
/// <summary>Arguments for the <c>read_document_chunk</c> tool.</summary>
public class ReadDocumentChunkRequest
{
    [JsonPropertyName("document_id")]
    public required string DocumentId { get; set; }

    [JsonPropertyName("chunk_index")]
    public int? ChunkIndex { get; set; }
}

// RAG: добавлено 2026-09-17
/// <summary>A single search result item, serialized to the model as JSON.</summary>
public class DocumentSearchResultItem
{
    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = null!;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = null!;

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = null!;
}

// RAG: добавлено 2026-09-17
/// <summary>A single document chunk read for the <c>read_document_chunk</c> tool.</summary>
public class DocumentChunkResult
{
    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = null!;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = null!;

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = null!;

    // RAG_DOCUMENTS: добавлено 2026-09-24 (ревью L6) — ответ ограничен страницей (~12 000 символов);
    // эти поля говорят модели, какие фрагменты она получила и как читать дальше.

    /// <summary>Index of the last chunk included in <see cref="Content"/>.</summary>
    [JsonPropertyName("last_chunk_index")]
    public int LastChunkIndex { get; set; }

    [JsonPropertyName("total_chunks")]
    public int TotalChunks { get; set; }

    /// <summary>The <c>chunk_index</c> to pass to read the next page; null at the end of the document.</summary>
    [JsonPropertyName("next_chunk_index")]
    public int? NextChunkIndex { get; set; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
