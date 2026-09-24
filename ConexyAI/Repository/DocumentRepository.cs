using System.Data;
using System.Data.Common;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ConexyAI.Repository;

// RAG: добавлено 2026-09-17
public class DocumentRepository : IDocumentRepository
{
    private readonly DbConexy _context;

    public DocumentRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<Document> AddDocumentAsync(Document document, CancellationToken ct = default)
    {
        _context.Documents.Add(document);
        await _context.SaveChangesAsync(ct);
        return document;
    }

    public async Task<Document?> GetDocumentAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        return await _context.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<Document>> ListDocumentsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.Documents
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<DocumentChunk?> GetChunkAsync(Guid userId, Guid documentId, int? chunkIndex, CancellationToken ct = default)
    {
        if (chunkIndex is { } idx)
        {
            return await _context.DocumentChunks
                .AsNoTracking()
                .Where(c => c.DocumentId == documentId && c.Document.UserId == userId)
                .FirstOrDefaultAsync(c => c.ChunkIndex == idx, ct);
        }

        // No index: return the first chunk as a lightweight placeholder (full text is
        // assembled by the service from all chunks).
        return await _context.DocumentChunks
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId && c.Document.UserId == userId)
            .OrderBy(c => c.ChunkIndex)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetChunksAsync(Guid userId, Guid documentId, CancellationToken ct = default)
    {
        return await _context.DocumentChunks
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId && c.Document.UserId == userId)
            .OrderBy(c => c.ChunkIndex)
            .ToListAsync(ct);
    }

    // RAG_DOCUMENTS: добавлено 2026-09-24 (ревью L6)
    public async Task<int> CountChunksAsync(Guid userId, Guid documentId, CancellationToken ct = default)
    {
        return await _context.DocumentChunks
            .AsNoTracking()
            .CountAsync(c => c.DocumentId == documentId && c.Document.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<DocumentChunk>> GetChunkRangeAsync(Guid userId, Guid documentId, int fromIndex, int take, CancellationToken ct = default)
    {
        return await _context.DocumentChunks
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId && c.Document.UserId == userId && c.ChunkIndex >= fromIndex)
            .OrderBy(c => c.ChunkIndex)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    // RAG_DOCUMENTS: добавлено 2026-09-24 (ревью M9)
    public async Task<Document?> DeleteDocumentAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        // The owner filter is the security boundary: a foreign id matches nothing.
        var document = await _context.Documents.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct);
        if (document is null)
            return null;

        await RemoveWithChunksAsync(document, ct);
        await _context.SaveChangesAsync(ct);
        return document;
    }

    public async Task<IReadOnlyList<Document>> DeleteUserDocumentsAsync(Guid userId, CancellationToken ct = default)
    {
        var documents = await _context.Documents.Where(d => d.UserId == userId).ToListAsync(ct);
        foreach (var document in documents)
        {
            await RemoveWithChunksAsync(document, ct);
        }
        if (documents.Count > 0)
            await _context.SaveChangesAsync(ct);
        return documents;
    }

    /// <summary>
    /// Marks the document and its chunks deleted. Chunks are removed by key (their text is never
    /// loaded), and explicitly: the in-memory provider used by the tests has no database-level cascade,
    /// and ExecuteDelete is not available there.
    /// </summary>
    private async Task RemoveWithChunksAsync(Document document, CancellationToken ct)
    {
        var chunkIds = await _context.DocumentChunks
            .Where(c => c.DocumentId == document.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);
        foreach (var chunkId in chunkIds)
        {
            var chunk = _context.DocumentChunks.Local.FirstOrDefault(c => c.Id == chunkId)
                        ?? new DocumentChunk { Id = chunkId, DocumentId = document.Id, Content = string.Empty };
            _context.DocumentChunks.Remove(chunk);
        }
        _context.Documents.Remove(document);
    }

    public async Task<IReadOnlyList<DocumentSearchRow>> SearchAsync(
        Guid userId,
        string query,
        int limit,
        string? documentName,
        string textSearchConfig,
        CancellationToken ct = default)
    {
        var config = IsSafeConfig(textSearchConfig) ? textSearchConfig : "simple";

        var sql = $"""
            SELECT c."Id", c."DocumentId", c."ChunkIndex", c."Content", d."FileName", d."Title",
                   ts_rank(to_tsvector('{config}', c."Content"), websearch_to_tsquery('{config}', @q))::float8 AS "Score"
            FROM "document_chunk" c
            INNER JOIN "document" d ON d."Id" = c."DocumentId"
            WHERE d."UserId" = @userId
              AND to_tsvector('{config}', c."Content") @@ websearch_to_tsquery('{config}', @q)
            {(documentName is null ? "" : "AND d.\"FileName\" ILIKE @name")}
            ORDER BY "Score" DESC
            LIMIT @limit
            """;

        var connection = _context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            var u = command.CreateParameter();
            u.ParameterName = "userId";
            u.Value = userId;
            command.Parameters.Add(u);

            var q = command.CreateParameter();
            q.ParameterName = "q";
            q.Value = query;
            command.Parameters.Add(q);

            if (documentName is not null)
            {
                var n = command.CreateParameter();
                n.ParameterName = "name";
                n.Value = "%" + documentName + "%";
                command.Parameters.Add(n);
            }

            var l = command.CreateParameter();
            l.ParameterName = "limit";
            l.Value = Math.Clamp(limit, 1, 50);
            command.Parameters.Add(l);

            var results = new List<DocumentSearchRow>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new DocumentSearchRow
                {
                    Id = reader.GetGuid(0),
                    DocumentId = reader.GetGuid(1),
                    ChunkIndex = reader.GetInt32(2),
                    Content = reader.GetString(3),
                    FileName = reader.GetString(4),
                    Title = reader.GetString(5),
                    Score = reader.GetDouble(6)
                });
            }

            return results;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static bool IsSafeConfig(string config) =>
        string.Equals(config, "russian", StringComparison.OrdinalIgnoreCase)
        || string.Equals(config, "english", StringComparison.OrdinalIgnoreCase)
        || string.Equals(config, "simple", StringComparison.OrdinalIgnoreCase);
}
