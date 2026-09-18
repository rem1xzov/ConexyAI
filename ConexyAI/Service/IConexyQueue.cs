using ConexyAI.Contract;

public interface IConexyQueue
{
    ValueTask EnqueueAsync(ConexyJob job, CancellationToken ct = default);
    IAsyncEnumerable<ConexyJob> ReadAllAsync(CancellationToken ct = default);
}