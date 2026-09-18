using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Repository;

namespace ConexyAI.Service;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Reads and writes a user's durable memory facts (flat text list) and builds the prompt
/// injection block for chat/agent system prompts. Fact extraction itself runs in the
/// background <see cref="MemoryExtractionWorker"/>.
/// </summary>
public interface IUserMemoryService
{
    Task<IReadOnlyList<string>> GetFactsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Enqueues a background extraction run for the given chat.</summary>
    void EnqueueExtraction(Guid userId, Guid chatId);

    /// <summary>Returns the "Известные факты о пользователе" block, or empty string if no facts.</summary>
    Task<string> BuildPromptBlockAsync(Guid userId, CancellationToken ct = default);
}

public class UserMemoryService : IUserMemoryService
{
    private readonly IUserMemoryRepository _repository;
    private readonly IMemoryExtractionQueue _queue;

    public UserMemoryService(IUserMemoryRepository repository, IMemoryExtractionQueue queue)
    {
        _repository = repository;
        _queue = queue;
    }

    public async Task<IReadOnlyList<string>> GetFactsAsync(Guid userId, CancellationToken ct = default)
    {
        var facts = await _repository.GetFactsAsync(userId, ct);
        return facts.Select(f => f.FactText).ToList();
    }

    public void EnqueueExtraction(Guid userId, Guid chatId)
    {
        // Fire-and-forget with error logging handled by the worker; never block the user.
        _ = _queue.EnqueueAsync(new MemoryExtractionJob(userId, chatId)).AsTask();
    }

    public async Task<string> BuildPromptBlockAsync(Guid userId, CancellationToken ct = default)
    {
        var facts = await GetFactsAsync(userId, ct);
        if (facts.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\nИзвестные факты о пользователе:");
        foreach (var fact in facts)
        {
            sb.AppendLine($"- {fact}");
        }
        return sb.ToString();
    }
}
