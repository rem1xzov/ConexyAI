using System.Text.RegularExpressions;
using ConexyAI.Hub;
using ConexyAI.Repository;
using ConexyAI.Service.Push;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service.Phone;

// PHONE_AGENT: добавлено 2026-10-29 — push «задача готова» и «агенту нужен телефон», ожидание подключения.

public interface IPhoneNotifier
{
    /// <summary>
    /// Телефон нужен прямо сейчас. Подключён — true сразу. Иначе ждёт подключения (до ~60 с), шлёт push
    /// <c>phone_needed</c> и ждёт ещё несколько минут. False — связи нет, агент должен остановиться.
    /// </summary>
    Task<bool> EnsurePhoneOnlineAsync(Guid userId, Guid taskId, Guid chatId, CancellationToken ct);

    /// <summary>Push <c>completed</c>, если приложение не на экране. Ничего не делает, когда push не настроен.</summary>
    Task NotifyCompletedAsync(Guid userId, Guid taskId, Guid chatId, string? answer, bool incognito, CancellationToken ct);
}

public sealed class PhoneNotifier : IPhoneNotifier
{
    public static readonly TimeSpan DefaultConnectWait = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultPushWait = TimeSpan.FromMinutes(4);
    private const string DoneTitle = "Задача готова";

    private readonly IPhoneToolBridge _bridge;
    private readonly IPushService _push;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<ConexyHub> _hub;
    private readonly ILogger<PhoneNotifier> _logger;
    private readonly TimeSpan _connectWait;
    private readonly TimeSpan _pushWait;

    public PhoneNotifier(
        IPhoneToolBridge bridge,
        IPushService push,
        IServiceScopeFactory scopes,
        IHubContext<ConexyHub> hub,
        ILogger<PhoneNotifier> logger,
        TimeSpan? connectWait = null,
        TimeSpan? pushWait = null)
    {
        _bridge = bridge;
        _push = push;
        _scopes = scopes;
        _hub = hub;
        _logger = logger;
        _connectWait = connectWait ?? DefaultConnectWait;
        _pushWait = pushWait ?? DefaultPushWait;
    }

    public async Task<bool> EnsurePhoneOnlineAsync(Guid userId, Guid taskId, Guid chatId, CancellationToken ct)
    {
        if (_bridge.IsOnline(userId))
            return true;

        await SendStatusAsync(taskId, "Жду подключения телефона…");
        if (await _bridge.WaitForOnlineAsync(userId, _connectWait, ct))
            return true;

        await SafePushAsync(userId, new Dictionary<string, string>
        {
            ["type"] = "phone_needed",
            ["taskId"] = taskId.ToString(),
            ["chatId"] = chatId.ToString(),
            ["title"] = "Агенту нужен телефон",
            ["body"] = "Откройте приложение ConexyAI, чтобы агент продолжил работу.",
        });

        return await _bridge.WaitForOnlineAsync(userId, _pushWait, ct);
    }

    public async Task NotifyCompletedAsync(Guid userId, Guid taskId, Guid chatId, string? answer, bool incognito, CancellationToken ct)
    {
        if (!_push.IsConfigured || _bridge.IsForeground(userId))
            return;

        var title = DoneTitle;
        var body = "Ответ готов — откройте приложение.";
        if (!incognito)
        {
            title = await ChatTitleAsync(userId, chatId, ct) ?? DoneTitle;
            var preview = Preview(answer);
            if (preview.Length > 0)
                body = preview;
        }

        await SafePushAsync(userId, new Dictionary<string, string>
        {
            ["type"] = "completed",
            ["taskId"] = taskId.ToString(),
            ["chatId"] = incognito ? string.Empty : chatId.ToString(),
            ["title"] = title,
            ["body"] = body,
        });
    }

    private async Task<string?> ChatTitleAsync(Guid userId, Guid chatId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var chat = await scope.ServiceProvider.GetRequiredService<IChatHistoryRepository>().GetChatAsync(userId, chatId, ct);
            var title = !string.IsNullOrWhiteSpace(chat?.Title) ? chat!.Title : chat?.FirstUserMessage;
            if (string.IsNullOrWhiteSpace(title))
                return null;
            title = Regex.Replace(title, @"\s+", " ").Trim();
            return title.Length <= 60 ? title : title[..60].TrimEnd() + "…";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the chat title for the push.");
            return null;
        }
    }

    /// <summary>Начало ответа без разметки, не длиннее ~140 символов.</summary>
    public static string Preview(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return string.Empty;
        var text = Regex.Replace(answer, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"[`*_#>|~]", string.Empty);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 140 ? text : text[..140].TrimEnd() + "…";
    }

    private async Task SafePushAsync(Guid userId, IReadOnlyDictionary<string, string> data)
    {
        try
        {
            await _push.SendAsync(userId, data, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Push '{Type}' failed: {Error}.", data.GetValueOrDefault("type"), ex.GetType().Name);
        }
    }

    private async Task SendStatusAsync(Guid taskId, string label)
    {
        try
        {
            await _hub.Clients.Group($"task_{taskId}").SendAsync("OnAgentStatus", new { stage = "executing", label }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send phone status for task {TaskId}.", taskId);
        }
    }
}
