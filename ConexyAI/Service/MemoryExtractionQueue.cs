using System.Threading.Channels;

namespace ConexyAI.Service;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public record MemoryExtractionJob(Guid UserId, Guid ChatId);

/// <summary>Bounded channel of pending user-memory extraction jobs.</summary>
public interface IMemoryExtractionQueue
{
    ValueTask EnqueueAsync(MemoryExtractionJob job, CancellationToken ct = default);
    IAsyncEnumerable<MemoryExtractionJob> ReadAllAsync(CancellationToken ct = default);
}

public class MemoryExtractionQueue : IMemoryExtractionQueue
{
    private readonly Channel<MemoryExtractionJob> _channel = Channel.CreateBounded<MemoryExtractionJob>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(MemoryExtractionJob job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<MemoryExtractionJob> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
