using System.Threading;

namespace ConexyAI.Service;

// ORCHESTRA: добавлено 2026-10-12 — «личность» агента, который прямо сейчас выполняет вызов
// инструмента. Ведущий агент и каждый помощник идут своим async-потоком, поэтому идентификатор
// едет в AsyncLocal: сервисы (bash, редактор, подтверждения) читают его при отправке события и
// проставляют в ToolActionEvent/PendingActionEvent — так фронт понимает, ЧЕЙ это шаг и ЧЬЯ команда
// ждёт подтверждения, и рисует отдельную карточку на каждого агента.
/// <summary>Cosmetic team role of an agent; free-form for roles the lead invents.</summary>
public sealed record AgentIdentity(string Id, string Role, IReadOnlyList<string> Scope)
{
    /// <summary>The single lead agent — the only one allowed to launch workers.</summary>
    public static AgentIdentity Lead { get; } = new("lead", "lead", Array.Empty<string>());

    /// <summary>True when <paramref name="relativePath"/> is inside one of the agent's owned prefixes.</summary>
    public bool Owns(string relativePath)
    {
        if (Scope.Count == 0) return true; // the lead owns the whole workspace
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var prefix in Scope)
        {
            var p = prefix.Replace('\\', '/').Trim('/');
            if (p.Length == 0) return true;
            if (path.Equals(p, StringComparison.Ordinal) || path.StartsWith(p + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>Ambient (async-local) identity of the running agent; null outside an agent turn.</summary>
public static class AgentExecutionContext
{
    private static readonly AsyncLocal<AgentIdentity?> _current = new();

    public static AgentIdentity? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public static string? AgentId => _current.Value?.Id;
    public static string? AgentRole => _current.Value?.Role;
}
