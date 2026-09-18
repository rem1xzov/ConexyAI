using System.Threading.Channels;
using ConexyAI.Contract;

namespace ConexyAI.Service;

public class ConexyQueue : IConexyQueue
{
    private readonly Channel<ConexyJob> _channel = Channel.CreateBounded<ConexyJob>(new BoundedChannelOptions(200)
    {
        FullMode = BoundedChannelFullMode.Wait
    });

    public ValueTask EnqueueAsync(ConexyJob job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<ConexyJob> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}