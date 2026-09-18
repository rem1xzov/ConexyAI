using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Repository;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// RAG: добавлено 2026-09-17
/// <summary>
/// Ingestion + retrieval for the RAG document knowledge base. Ingestion parses a file,
/// chunks it, and stores it; retrieval uses PostgreSQL full-text search
/// (<c>to_tsvector</c>/<c>websearch_to_tsquery</c>) as the reliable fallback when embeddings
/// are not configured.
/// </summary>
public interface IDocumentService
{
    Task<Document> IndexAsync(byte[] bytes, string fileName, string contentType, Guid userId, CancellationToken ct = default);
    Task<string> SearchJsonAsync(Guid userId, string query, int limit, string? documentName, CancellationToken ct = default);
    Task<string> ReadChunkJsonAsync(Guid userId, Guid documentId, int? chunkIndex, CancellationToken ct = default);
}

public class DocumentService : IDocumentService
{
    private readonly IDocumentRepository _repository;
    private readonly IOptions<RagOptions> _options;

    public DocumentService(IDocumentRepository repository, IOptions<RagOptions> options)
    {
        _repository = repository;
        _options = options;
    }

    public async Task<Document> IndexAsync(byte[] bytes, string fileName, string contentType, Guid userId, CancellationToken ct = default)
    {
        var text = DocumentParser.Parse(bytes, fileName);
        var chunks = DocumentChunker.Chunk(text, _options.Value.ChunkSize, _options.Value.ChunkOverlap);

        var document = new Document
        {
            UserId = userId,
            Title = Path.GetFileNameWithoutExtension(fileName),
            FileName = fileName,
            ContentType = contentType,
            FilePath = await PersistSourceAsync(bytes, fileName, ct)
        };

        for (var i = 0; i < chunks.Count; i++)
        {
            document.Chunks.Add(new DocumentChunk
            {
                DocumentId = document.Id,
                ChunkIndex = i,
                Content = chunks[i],
                TokensCount = DocumentChunker.EstimateTokens(chunks[i])
            });
        }

        await _repository.AddDocumentAsync(document, ct);
        return document;
    }

    public async Task<string> SearchJsonAsync(Guid userId, string query, int limit, string? documentName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.Serialize(new { results = Array.Empty<DocumentSearchResultItem>() });

        var rows = await _repository.SearchAsync(
            userId, query, limit <= 0 ? 5 : limit, documentName, _options.Value.TextSearchConfig, ct);

        var results = rows.Select(r => new DocumentSearchResultItem
        {
            DocumentId = r.DocumentId.ToString(),
            FileName = r.FileName,
            ChunkIndex = r.ChunkIndex,
            Score = Math.Round(r.Score, 4),
            Content = r.Content
        }).ToList();

        return JsonSerializer.Serialize(new { results });
    }

    public async Task<string> ReadChunkJsonAsync(Guid userId, Guid documentId, int? chunkIndex, CancellationToken ct = default)
    {
        var document = await _repository.GetDocumentAsync(userId, documentId, ct);
        if (document is null)
            return JsonSerializer.Serialize(new { error = "Document not found." });

        if (chunkIndex is { } idx)
        {
            var chunk = await _repository.GetChunkAsync(userId, documentId, idx, ct);
            if (chunk is null)
                return JsonSerializer.Serialize(new { error = $"Chunk {idx} not found in document '{document.FileName}'." });

            return JsonSerializer.Serialize(new DocumentChunkResult
            {
                DocumentId = documentId.ToString(),
                FileName = document.FileName,
                ChunkIndex = chunk.ChunkIndex,
                Content = chunk.Content
            });
        }

        var chunks = await _repository.GetChunksAsync(userId, documentId, ct);
        var fullText = string.Join("\n\n", chunks.Select(c => c.Content));

        return JsonSerializer.Serialize(new DocumentChunkResult
        {
            DocumentId = documentId.ToString(),
            FileName = document.FileName,
            ChunkIndex = -1, // -1 = full document text
            Content = fullText
        });
    }

    private async Task<string> PersistSourceAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        var dir = ResolveDocumentsDirectory();
        Directory.CreateDirectory(dir);

        var safeName = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}_{safeName}");
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    private string ResolveDocumentsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.Value.DocumentsDirectory))
            return Path.GetFullPath(_options.Value.DocumentsDirectory);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConexyAI", "rag_documents");
    }
}
