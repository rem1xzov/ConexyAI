using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Extensions;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// RAG: добавлено 2026-09-17
[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IDocumentRepository _documentRepository;

    public DocumentController(IDocumentService documentService, IDocumentRepository documentRepository)
    {
        _documentService = documentService;
        _documentRepository = documentRepository;
    }

    /// <summary>Uploads a document (TXT/MD/DOCX/PDF) and indexes it into the RAG knowledge base.</summary>
    [HttpPost]
    [RequestSizeLimit(55_000_000)]
    public async Task<ActionResult<Document>> Upload([FromForm] IFormFile file, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var document = await _documentService.IndexAsync(ms.ToArray(), file.FileName, file.ContentType, userId, ct);

        return Ok(new { id = document.Id, title = document.Title, fileName = document.FileName, chunks = document.Chunks.Count });
    }

    /// <summary>Lists indexed documents in the knowledge base.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Document>>> List(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var documents = await _documentRepository.ListDocumentsAsync(userId, ct);
        return Ok(documents.Select(d => new { d.Id, d.Title, d.FileName, d.ContentType, d.CreatedAt }));
    }

    /// <summary>Runs a full-text search over indexed documents (used for testing/debugging).</summary>
    [HttpPost("search")]
    public async Task<ActionResult<string>> Search([FromBody] SearchDocumentsRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (request is null || string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "Query is required." });

        return Content(await _documentService.SearchJsonAsync(userId, request.Query, request.Limit ?? 5, request.DocumentName, ct), "application/json");
    }

    private bool TryGetUserId(out Guid userId) => User.TryGetUserId(out userId);
}
