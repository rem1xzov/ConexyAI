using System.Threading.Channels;

namespace ConexyAI.Service;

// CHAT_TITLE_TOPIC: добавлено 2026-10-01.
//
// Название чата раньше было сырым первым сообщением пользователя (обрубленным до превью). Теперь
// название — это тема разговора, которую выводит дешёвая модель (например «Игра Тетрис» вместо
// «привет, сделай пожалуйста тетрис, вот описание...»). Генерация идёт в фоне, чтобы ни секунды не
// задерживать ответ пользователю.
public record ChatTitleJob(Guid UserId, Guid ChatId, string FirstUserMessage);

/// <summary>Bounded channel of pending chat-title generation jobs.</summary>
public interface IChatTitleQueue
{
    ValueTask EnqueueAsync(ChatTitleJob job, CancellationToken ct = default);
    IAsyncEnumerable<ChatTitleJob> ReadAllAsync(CancellationToken ct = default);
}

public class ChatTitleQueue : IChatTitleQueue
{
    private readonly Channel<ChatTitleJob> _channel = Channel.CreateBounded<ChatTitleJob>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(ChatTitleJob job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<ChatTitleJob> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
