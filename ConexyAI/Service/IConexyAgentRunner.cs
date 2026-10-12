using ConexyAI.Contract;
using ConexyAI.Model;

namespace ConexyAI.Service;

public interface IConexyAgentRunner
{
    // CONVERSATION_SERVICE: добавлено 2026-09-23 — контекст строится один раз вызывающим и
    // передаётся сюда, чтобы сборка запроса и запись хода (в finally вызывающего) использовали
    // один и тот же ConversationContext и не могли разойтись.
    Task<string> RunLoopAsync(ConexyJob job, ConversationContext context, CancellationToken ct = default);

    // CONVERSATION_SERVICE: системный промпт агента нужен вызывающему, чтобы построить контекст.
    // COWORK_MODE: у каждого агентского режима свой промпт, поэтому это метод от режима.
    string GetSystemPrompt(ConexyModelType modelType);

    // ORCHESTRA: добавлено 2026-09-28, расширено 2026-10-12 — прогон агента-исполнителя команды
    // (роль + scope + полный набор инструментов). Возвращает его отчёт; используется spawn_agents.
    Task<string> RunWorkerAsync(ConexyJob job, AgentIdentity identity, string brief, CancellationToken ct = default);

    // PARTIAL_TURN_PERSIST: добавлено 2026-09-22
    /// <summary>
    /// Everything the agent has already streamed to the client during the current run. The worker
    /// reads it after a stop/failure so the turn can still be stored in the chat history — without
    /// it, every stopped run left the conversation empty and the next message had no context.
    /// </summary>
    string PartialOutput { get; }
}