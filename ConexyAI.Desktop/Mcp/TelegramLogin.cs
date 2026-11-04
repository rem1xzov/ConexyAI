using System.Diagnostics;
using System.Text;

namespace ConexyAI.Desktop.Mcp;

/// <summary>
/// TELEGRAM_LOGIN: вход в личный аккаунт Telegram без ручной «session string».
/// <para>
/// Telegram (MTProto) не пускает в аккаунт по одним api_id/api_hash — это ключи ПРИЛОЖЕНИЯ, а не входа:
/// владелец аккаунта обязан один раз подтвердить новое устройство. Поэтому клиент сам запускает вход
/// (Telethon через uv): показывает QR в консоли или отдаёт ссылку tg://login на сайт/телефон, где
/// подтверждение — одно касание в Telegram. Пароль 2FA спрашивается, если он включён. Готовая сессия
/// сохраняется в mcp.json зашифрованной, MCP-сервер перезапускается — больше ничего вставлять не нужно.
/// </para>
/// </summary>
public static class TelegramLogin
{
    // QR печатается в stderr (видно в консоли), служебные строки — в stdout (читает клиент).
    private const string Script = """
import asyncio, os, sys
from telethon import TelegramClient
from telethon.sessions import StringSession
from telethon.errors import SessionPasswordNeededError, PasswordHashInvalidError

def out(line):
    sys.stdout.write(line + "\n"); sys.stdout.flush()

def proxy():
    url = os.environ.get("TG_PROXY")
    if not url:
        return None
    from urllib.parse import urlparse, unquote
    u = urlparse(url)
    kind = "socks5" if u.scheme.startswith("socks5") else ("socks4" if u.scheme == "socks4" else "http")
    return dict(proxy_type=kind, addr=u.hostname, port=u.port, rdns=True,
                username=unquote(u.username) if u.username else None,
                password=unquote(u.password) if u.password else None)

async def main():
    client = TelegramClient(StringSession(), int(os.environ["TG_API_ID"]), os.environ["TG_API_HASH"], proxy=proxy())
    await client.connect()
    show_qr = os.environ.get("TG_SHOW_QR") == "1"
    while True:
        qr = await client.qr_login()
        out("URL:" + qr.url)
        if show_qr:
            try:
                import qrcode
                q = qrcode.QRCode(border=1); q.add_data(qr.url); q.make()
                sys.stderr.write("\n"); q.print_ascii(out=sys.stderr, invert=True); sys.stderr.flush()
            except Exception as e:
                sys.stderr.write("QR: " + str(e) + "\n")
        try:
            await qr.wait(timeout=110)
            break
        except asyncio.TimeoutError:
            out("EXPIRED")
        except SessionPasswordNeededError:
            while True:
                out("PASSWORD")
                pw = sys.stdin.readline().strip()
                if not pw:
                    out("ERROR:Нужен пароль двухэтапной проверки Telegram.")
                    return
                try:
                    await client.sign_in(password=pw)
                    break
                except PasswordHashInvalidError:
                    out("BADPASSWORD")
            break
    me = await client.get_me()
    out("USER:" + ((me.first_name or "") + (" @" + me.username if me.username else "")).strip())
    out("SESSION:" + client.session.save())
    await client.disconnect()

try:
    asyncio.run(main())
except Exception as e:
    out("ERROR:" + type(e).__name__ + ": " + str(e))
""";

    public enum State { Starting, WaitingScan, NeedPassword, Done, Failed }

    /// <summary>Один вход (консольный или с сайта). Читает stdout скрипта и хранит состояние.</summary>
    public sealed class Attempt : IDisposable
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<bool> _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();

        public string ApiId { get; }
        public string ApiHash { get; }
        public State State { get; private set; } = State.Starting;
        public string? Url { get; private set; }
        public string? Session { get; private set; }
        public string? User { get; private set; }
        public string? Error { get; private set; }
        public bool BadPassword { get; private set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        internal Attempt(Process process, string apiId, string apiHash)
        {
            _process = process;
            ApiId = apiId;
            ApiHash = apiHash;
            _ = Task.Run(ReadLoopAsync);
        }

        /// <summary>Ждёт следующего изменения состояния (или таймаута).</summary>
        public async Task WaitChangeAsync(TimeSpan timeout, CancellationToken ct)
        {
            Task signal;
            lock (_sync)
                signal = _signal.Task;
            await Task.WhenAny(signal, Task.Delay(timeout, ct));
            ct.ThrowIfCancellationRequested();
        }

        public void SubmitPassword(string password)
        {
            if (State != State.NeedPassword)
                return;
            BadPassword = false;
            Set(State.WaitingScan);
            _process.StandardInput.WriteLine(password);
            _process.StandardInput.Flush();
        }

        private void Set(State state)
        {
            TaskCompletionSource<bool> previous;
            lock (_sync)
            {
                State = state;
                previous = _signal;
                _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previous.TrySetResult(true);
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.StartsWith("URL:", StringComparison.Ordinal))
                    {
                        Url = line[4..];
                        Set(State.WaitingScan);
                    }
                    else if (line == "PASSWORD")
                        Set(State.NeedPassword);
                    else if (line == "BADPASSWORD")
                        BadPassword = true;
                    else if (line.StartsWith("USER:", StringComparison.Ordinal))
                        User = line[5..];
                    else if (line.StartsWith("SESSION:", StringComparison.Ordinal))
                    {
                        Session = line[8..];
                        Set(State.Done);
                    }
                    else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                    {
                        Error = line[6..];
                        Set(State.Failed);
                    }
                }
                await _process.WaitForExitAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Error ??= ex.Message;
            }
            if (State is not (State.Done or State.Failed))
            {
                Error ??= "Вход прерван.";
                Set(State.Failed);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }

    /// <summary>
    /// TELEGRAM_PROXY: Telegram ходит по своему протоколу (MTProto) и системный HTTP-прокси не использует — прокси
    /// передаётся явно. Порядок: свой прокси ПК (/proxy) → прокси сервера (только админу) → напрямую.
    /// </summary>
    public static async Task<string?> ResolveProxyAsync(CancellationToken ct)
    {
        var local = ClientConfig.LoadProxy();
        if (local is not null)
            return local.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : local;
        return ServerClient.Current is { } server ? await server.GetAgentProxyAsync(ct) : null;
    }

    public static bool IsValidProxy(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
        && u.Scheme is "socks5" or "socks5h" or "socks4" or "http" or "https"
        && !string.IsNullOrEmpty(u.Host) && u.Port > 0;

    /// <summary>Адрес без логина и пароля — для сообщений.</summary>
    public static string DescribeProxy(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}:{u.Port}" : "напрямую";

    /// <summary>Запускает вход. <paramref name="showQr"/> — печатать QR в консоль (интерактивный клиент).</summary>
    public static Attempt Start(string apiId, string apiHash, bool showQr, string? proxy = null)
    {
        if (!int.TryParse(apiId.Trim(), out _))
            throw new InvalidOperationException("api_id — это число из карточки приложения на my.telegram.org.");
        if (apiHash.Trim().Length < 16)
            throw new InvalidOperationException("api_hash выглядит неверно — скопируйте его из карточки приложения на my.telegram.org.");
        if (!StdioMcpClient.IsOnPath("uv"))
            throw new InvalidOperationException("Не найден uv. Установите: winget install --id=astral-sh.uv — затем повторите.");

        var psi = new ProcessStartInfo("uv")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = !showQr,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = !showQr,
        };
        foreach (var arg in new[] { "run", "--no-project", "--quiet", "--with", "telethon>=1.36,<2", "--with", "qrcode>=7", "--with", "python-socks[asyncio]>=2", "python", "-c", Script })
            psi.ArgumentList.Add(arg);
        if (proxy is not null)
            psi.Environment["TG_PROXY"] = proxy.Trim();
        psi.Environment["TG_API_ID"] = apiId.Trim();
        psi.Environment["TG_API_HASH"] = apiHash.Trim();
        psi.Environment["TG_SHOW_QR"] = showQr ? "1" : "0";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить вход в Telegram.");
        if (!showQr)
            _ = process.StandardError.ReadToEndAsync();
        return new Attempt(process, apiId.Trim(), apiHash.Trim());
    }

    /// <summary>Сохраняет ключи и сессию в mcp.json (зашифрованно) и (пере)запускает Telegram MCP.</summary>
    public static async Task<IReadOnlyList<string>> SaveAsync(LocalTools? local, string apiId, string apiHash, string session, CancellationToken ct, string? proxy = null)
    {
        var entry = McpCatalog.Find("telegram") ?? throw new InvalidOperationException("Нет записи Telegram в каталоге.");
        var server = new McpServerEntry
        {
            Command = entry.Command,
            Args = entry.Args.ToList(),
            Env = entry.Env is null ? null : new Dictionary<string, string>(entry.Env),
            CatalogId = entry.Id,
        };
        server.SetSecret("TELEGRAM_API_ID", apiId);
        server.SetSecret("TELEGRAM_API_HASH", apiHash);
        server.SetSecret("TELEGRAM_SESSION_STRING", session);
        // TELEGRAM_PROXY: сам MCP-сервер тоже должен ходить через прокси (переменные chigwell/telegram-mcp).
        if (proxy is not null && Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var p))
        {
            server.Env ??= new Dictionary<string, string>();
            server.Env["TELEGRAM_PROXY_TYPE"] = p.Scheme.StartsWith("socks5", StringComparison.Ordinal) ? "socks5" : p.Scheme == "socks4" ? "socks4" : "http";
            server.Env["TELEGRAM_PROXY_HOST"] = p.Host;
            server.Env["TELEGRAM_PROXY_PORT"] = p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            server.Env["TELEGRAM_PROXY_RDNS"] = "true";
            if (!string.IsNullOrEmpty(p.UserInfo))
            {
                var parts = p.UserInfo.Split(':', 2);
                server.Env["TELEGRAM_PROXY_USERNAME"] = "${secret:TELEGRAM_PROXY_USERNAME}";
                server.SetSecret("TELEGRAM_PROXY_USERNAME", Uri.UnescapeDataString(parts[0]));
                if (parts.Length > 1)
                {
                    server.Env["TELEGRAM_PROXY_PASSWORD"] = "${secret:TELEGRAM_PROXY_PASSWORD}";
                    server.SetSecret("TELEGRAM_PROXY_PASSWORD", Uri.UnescapeDataString(parts[1]));
                }
            }
        }
        var config = McpConfig.Load();
        config.Servers[entry.Id] = server;
        config.Save();
        if (local is null)
            return [];
        var restart = local.Servers.Any(s => s.Name == McpConfig.Slug(entry.Id));
        return await local.StartAsync(ct, restart);
    }

    // ── Вход с сайта / телефона (через подключённый ПК) ─────────────────────

    private static Attempt? _remote;
    private static string? _remoteProxy;
    private static readonly object RemoteSync = new();

    /// <summary>telegram_login_start: начинает вход и ждёт ссылку tg://login (до 90 с — первый запуск качает пакеты).</summary>
    public static async Task<string> RemoteStartAsync(string apiId, string apiHash, CancellationToken ct)
    {
        var proxy = await ResolveProxyAsync(ct);
        Attempt attempt;
        lock (RemoteSync)
        {
            _remote?.Dispose();
            _remote = attempt = Start(apiId, apiHash, showQr: false, proxy);
            _remoteProxy = proxy;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (attempt.State == State.Starting && DateTime.UtcNow < deadline)
            await attempt.WaitChangeAsync(TimeSpan.FromSeconds(2), ct);
        return StatusJson(attempt);
    }

    /// <summary>telegram_login_status: текущее состояние; ждёт изменения до <paramref name="waitSeconds"/>.</summary>
    public static async Task<string> RemoteStatusAsync(LocalTools? local, int waitSeconds, CancellationToken ct)
    {
        var attempt = _remote;
        if (attempt is null)
            return """{"state":"idle"}""";
        if (attempt.State is State.Starting or State.WaitingScan)
            await attempt.WaitChangeAsync(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 25)), ct);
        if (attempt.State == State.Done && attempt.Session is { } session)
        {
            lock (RemoteSync)
            {
                if (_remote == attempt)
                    _remote = null;
            }
            await SaveAsync(local, attempt.ApiId, attempt.ApiHash, session, ct, _remoteProxy);
            attempt.Dispose();
        }
        return StatusJson(attempt);
    }

    public static string RemotePassword(string password)
    {
        var attempt = _remote;
        if (attempt is null)
            return """{"state":"idle"}""";
        attempt.SubmitPassword(password);
        return StatusJson(attempt);
    }

    public static void RemoteCancel()
    {
        lock (RemoteSync)
        {
            _remote?.Dispose();
            _remote = null;
        }
    }

    private static string StatusJson(Attempt a) => System.Text.Json.JsonSerializer.Serialize(new
    {
        state = a.State switch
        {
            State.Starting => "starting",
            State.WaitingScan => "waiting",
            State.NeedPassword => "password",
            State.Done => "done",
            _ => "failed",
        },
        url = a.State == State.WaitingScan ? a.Url : null,
        user = a.User,
        error = a.Error,
        badPassword = a.BadPassword,
    });
}
