using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// A page of the document (about <see cref="DocumentService.ReadPageChars"/> characters) starting
    /// at <paramref name="chunkIndex"/> (the beginning when null), with the index to continue from.
    /// </summary>
    Task<string> ReadChunkJsonAsync(Guid userId, Guid documentId, int? chunkIndex, CancellationToken ct = default);

    // RAG_DOCUMENTS: добавлено 2026-09-24 (ревью M9)
    /// <summary>Deletes the user's document: chunks, row and stored file. False when it is not the user's.</summary>
    Task<bool> DeleteAsync(Guid userId, Guid documentId, CancellationToken ct = default);

    /// <summary>Deletes every document of the user (rows and stored files); for account deletion.</summary>
    Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default);
}

public partial class DocumentService : IDocumentService
{
    /// <summary>
    /// RAG_DOCUMENTS: добавлено 2026-09-24 (ревью L6) — read_document_chunk без индекса отдавал весь
    /// документ целиком и переполнял контекст; теперь любой ответ — не больше страницы.
    /// </summary>
    public const int ReadPageChars = 12_000;

    private const int MaxChunksPerPage = 200;
    private const int MaxStoredNameChars = 100;

    private readonly IDocumentRepository _repository;
    private readonly IOptions<RagOptions> _options;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(IDocumentRepository repository, IOptions<RagOptions> options, ILogger<DocumentService>? logger = null)
    {
        _repository = repository;
        _options = options;
        _logger = logger ?? NullLogger<DocumentService>.Instance;
    }

    public async Task<Document> IndexAsync(byte[] bytes, string fileName, string contentType, Guid userId, CancellationToken ct = default)
    {
        // TEXT_DECODING: изменено 2026-09-24 (ревью M6) — текст и метаданные очищаются от NUL и прочих
        // символов, которые Postgres не принимает; имя укорачивается под колонку (500) и файловую систему.
        var name = TextSanitizer.Truncate(TextSanitizer.Clean(Path.GetFileName(fileName ?? string.Empty)).Trim(), 255);
        if (name.Length == 0) name = "document";

        var text = DocumentParser.Parse(bytes, name);
        var chunks = DocumentChunker.Chunk(text, _options.Value.ChunkSize, _options.Value.ChunkOverlap);

        var cleanType = TextSanitizer.Clean(contentType ?? string.Empty).Trim();
        var document = new Document
        {
            UserId = userId,
            Title = TextSanitizer.Truncate(Path.GetFileNameWithoutExtension(name), 500),
            FileName = TextSanitizer.Truncate(name, 500),
            ContentType = cleanType.Length is > 0 and <= 100 ? cleanType : "application/octet-stream",
        };

        for (var i = 0; i < chunks.Count; i++)
        {
            document.Chunks.Add(new DocumentChunk
            {
                DocumentId = document.Id,
                ChunkIndex = i,
                Content = TextSanitizer.Clean(chunks[i]),
                TokensCount = DocumentChunker.EstimateTokens(chunks[i])
            });
        }

        // The file is written only after parsing succeeded, and removed again when the row cannot be
        // saved: a failed upload no longer leaves an orphaned file behind (review M6).
        document.FilePath = await PersistSourceAsync(bytes, name, ct);
        try
        {
            await _repository.AddDocumentAsync(document, ct);
        }
        catch
        {
            DeleteStoredFile(document.FilePath);
            throw;
        }
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

        var total = await _repository.CountChunksAsync(userId, documentId, ct);
        var from = Math.Max(0, chunkIndex ?? 0);
        var chunks = await _repository.GetChunkRangeAsync(userId, documentId, from, MaxChunksPerPage, ct);
        if (chunks.Count == 0 || chunks[0].ChunkIndex != from)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Chunk {from} not found in document '{document.FileName}'.",
                total_chunks = total,
            });
        }

        // Adjacent chunks share an overlap; it is dropped so the page reads as continuous text.
        var content = new StringBuilder(chunks[0].Content.Length);
        var last = -1;
        string? previous = null;
        foreach (var chunk in chunks)
        {
            var piece = chunk.Content;
            if (previous is not null)
            {
                piece = WithoutOverlap(previous, chunk.Content, out var joined);
                if (!joined) piece = "\n" + piece;
                if (content.Length + piece.Length > ReadPageChars) break;
            }
            content.Append(piece);
            last = chunk.ChunkIndex;
            previous = chunk.Content;
        }

        var text = content.ToString();
        if (text.Length > ReadPageChars) text = TextSanitizer.Truncate(text, ReadPageChars);
        int? next = last + 1 < total ? last + 1 : null;

        return JsonSerializer.Serialize(new DocumentChunkResult
        {
            DocumentId = documentId.ToString(),
            FileName = document.FileName,
            ChunkIndex = from,
            LastChunkIndex = last,
            TotalChunks = total,
            NextChunkIndex = next,
            Content = text,
            Note = next is { } n
                ? $"Показаны фрагменты {from}–{last} из {total}. Чтобы читать дальше, вызови read_document_chunk с document_id \"{documentId}\" и chunk_index = {n}."
                : null,
        });
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid documentId, CancellationToken ct = default)
    {
        var document = await _repository.DeleteDocumentAsync(userId, documentId, ct);
        if (document is null)
            return false;

        DeleteStoredFile(document.FilePath);
        return true;
    }

    public async Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var documents = await _repository.DeleteUserDocumentsAsync(userId, ct);
        foreach (var document in documents)
        {
            DeleteStoredFile(document.FilePath);
        }
        return documents.Count;
    }

    /// <summary>
    /// <paramref name="next"/> without the text it shares with the end of <paramref name="previous"/>.
    /// <paramref name="joined"/> is false when no overlap was found (the chunks are joined by a newline).
    /// </summary>
    private string WithoutOverlap(string previous, string next, out bool joined)
    {
        var longest = Math.Min(Math.Min(previous.Length, next.Length), Math.Max(32, _options.Value.ChunkOverlap * 2));
        for (var length = longest; length >= 16; length--)
        {
            if (previous.AsSpan(previous.Length - length).SequenceEqual(next.AsSpan(0, length)))
            {
                joined = true;
                return next[length..];
            }
        }
        joined = false;
        return next;
    }

    private async Task<string> PersistSourceAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        var dir = ResolveDocumentsDirectory();
        Directory.CreateDirectory(dir);

        // Linux allows 255 bytes per file name; a long Cyrillic name used to fail the upload.
        var safeName = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
        var extension = Path.GetExtension(safeName);
        if (safeName.Length > MaxStoredNameChars)
        {
            var stem = Path.GetFileNameWithoutExtension(safeName);
            var room = Math.Max(1, MaxStoredNameChars - Math.Min(extension.Length, 20));
            safeName = TextSanitizer.Truncate(stem, room) + (extension.Length <= 20 ? extension : string.Empty);
        }

        var path = Path.Combine(dir, $"{Guid.NewGuid():N}_{safeName}");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        catch
        {
            DeleteStoredFile(path);
            throw;
        }
        return path;
    }

    /// <summary>
    /// Best-effort removal of a stored source file. Only files this service wrote ("{guid}_{name}")
    /// are touched, whatever the row says.
    /// </summary>
    private void DeleteStoredFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !StoredFileNameRegex().IsMatch(Path.GetFileName(path)))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete stored document file {Path}", path);
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}_")]
    private static partial Regex StoredFileNameRegex();

    private string ResolveDocumentsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.Value.DocumentsDirectory))
            return Path.GetFullPath(_options.Value.DocumentsDirectory);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConexyAI", "rag_documents");
    }
}
