using System.Collections.Concurrent;
using ConexyAI.Contract;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

public interface IConexyTodoService
{
    Task<TodoWriteResult> WriteAsync(Guid sessionId, TodoWriteRequest request, CancellationToken ct = default);
    void Clear(Guid sessionId);
}

/// <summary>
/// In-memory per-session todo state for the conexy-coder agent. Each todo_write fully
/// replaces the previous list (no merge). Registered as a singleton; <see cref="Clear"/>
/// is invoked by the background worker when a session finishes so memory does not grow
/// on the long-lived process.
/// </summary>
public class ConexyTodoService : IConexyTodoService
{
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.Ordinal)
    {
        "pending", "in_progress", "completed", "skipped",
    };

    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<ConexyTodoService> _logger;
    private readonly ConcurrentDictionary<Guid, List<TodoItem>> _state = new();

    public ConexyTodoService(IHubContext<ConexyHub> hubContext, ILogger<ConexyTodoService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<TodoWriteResult> WriteAsync(Guid sessionId, TodoWriteRequest request, CancellationToken ct = default)
    {
        var validationError = Validate(request.Todos);
        if (validationError is not null)
        {
            return validationError;
        }

        _state[sessionId] = request.Todos;

        await _hubContext.Clients.Group($"task_{sessionId}")
            .SendAsync("TodoUpdate", new TodoUpdateEvent { Todos = request.Todos }, ct);

        return new TodoWriteResult { Success = true };
    }

    public void Clear(Guid sessionId)
    {
        _state.TryRemove(sessionId, out _);
    }

    private static TodoWriteResult? Validate(List<TodoItem> todos)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var inProgressCount = 0;

        foreach (var item in todos)
        {
            if (!ValidStatuses.Contains(item.Status))
                return Fail("invalid_status", $"Invalid status '{item.Status}'.");

            if (item.Status == "skipped" && string.IsNullOrWhiteSpace(item.SkipReason))
                return Fail("missing_skip_reason", $"Todo '{item.Id}' is skipped but has no skip_reason.");

            if (!ids.Add(item.Id))
                return Fail("duplicate_id", $"Duplicate todo id '{item.Id}'.");

            if (item.Status == "in_progress")
                inProgressCount++;
        }

        if (inProgressCount > 1)
            return Fail("invalid_status", "Only one step can be in_progress at a time.");

        return null;
    }

    private static TodoWriteResult Fail(string errorType, string detail) =>
        new() { Success = false, ErrorType = errorType, ErrorDetail = detail };
}
