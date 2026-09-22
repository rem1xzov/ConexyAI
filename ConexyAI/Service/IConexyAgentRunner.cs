using ConexyAI.Contract;

namespace ConexyAI.Service;

public interface IConexyAgentRunner
{
    Task<string> RunLoopAsync(ConexyJob job, CancellationToken ct = default);

    // PARTIAL_TURN_PERSIST: добавлено 2026-09-22
    /// <summary>
    /// Everything the agent has already streamed to the client during the current run. The worker
    /// reads it after a stop/failure so the turn can still be stored in the chat history — without
    /// it, every stopped run left the conversation empty and the next message had no context.
    /// </summary>
    string PartialOutput { get; }
}