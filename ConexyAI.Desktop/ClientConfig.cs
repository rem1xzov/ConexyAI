using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConexyAI.Desktop;

/// <summary>
/// Настройки клиента: адрес сервера и ключ. Ключ на Windows шифруется DPAPI (привязка к учётной
/// записи пользователя), на других ОС лежит в файле с правами только для владельца.
/// Переменные окружения CONEXY_URL / CONEXY_KEY имеют приоритет над файлом.
/// </summary>
public sealed class ClientConfig
{
    public string? ServerUrl { get; set; }

    /// <summary>Ключ в защищённом виде (base64).</summary>
    public string? ProtectedKey { get; set; }

    public const string RemoteLevelSafe = "safe";
    public const string RemoteLevelFull = "full";

    /// <summary>
    /// REMOTE_APPROVAL_POLICY: что демон может выполнить по подтверждению с сайта. «safe» (по умолчанию) — без
    /// установки программ, отправки писем, команд с путями вне проекта, чтения вне проекта и сторонних MCP;
    /// «full» — всё с подтверждения на сайте. Меняется только локально: `conexy remote safe|full`.
    /// </summary>
    public string? RemoteApprovalLevel { get; set; }

    /// <summary>Уровень удалённых подтверждений из файла (читается на каждый запрос: демону не нужен перезапуск).</summary>
    public static string LoadRemoteApprovalLevel()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath))?.RemoteApprovalLevel is { } level
                && level.Equals(RemoteLevelFull, StringComparison.OrdinalIgnoreCase))
                return RemoteLevelFull;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return RemoteLevelSafe;
    }

    /// <summary>Сохраняет уровень прямо в файл (без Load(): CONEXY_URL из окружения не должен попасть в config.json).</summary>
    public static void SaveRemoteApprovalLevel(string level)
    {
        ClientConfig config;
        try
        {
            config = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath)) ?? new ClientConfig()
                : new ClientConfig();
        }
        catch (JsonException)
        {
            config = new ClientConfig();
        }
        config.RemoteApprovalLevel = level;
        config.Save();
    }

    /// <summary>
    /// SERVER_URL_HTTPS: ключ и команды агента идут по этому адресу — только https; http допустим лишь для
    /// localhost/127.0.0.1/::1 (локальная разработка).
    /// </summary>
    public static bool IsAllowedServerUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static bool _urlWarned;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI", "config.json");

    public static ClientConfig Load()
    {
        var config = new ClientConfig();
        if (File.Exists(FilePath))
        {
            try
            {
                config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath)) ?? config;
            }
            catch (JsonException)
            {
                // Битый файл — как будто входа не было; login перезапишет.
            }
        }

        // SERVER_URL_HTTPS: старый конфиг с http-адресом — как будто входа не было (клиент спросит адрес заново).
        var saved = config.ServerUrl;
        if (saved is not null && !IsAllowedServerUrl(saved))
        {
            WarnOnce($"ВНИМАНИЕ: сохранённый адрес сервера {saved} не https — не используется. Выполните conexy login с https-адресом.");
            config.ServerUrl = saved = null;
        }

        if (Environment.GetEnvironmentVariable("CONEXY_URL") is { Length: > 0 } url)
        {
            if (!IsAllowedServerUrl(url))
                WarnOnce($"ВНИМАНИЕ: CONEXY_URL={url} проигнорирован — допустим только https (http — лишь для localhost).");
            else
            {
                WarnIfHostChanged(saved, url, "CONEXY_URL");
                config.ServerUrl = url;
            }
        }
        return config;
    }

    /// <summary>Громкое предупреждение: ключ уйдёт на другой хост, чем тот, что сохранён при входе.</summary>
    public static void WarnIfHostChanged(string? saved, string url, string source)
    {
        if (saved is null || !Uri.TryCreate(saved, UriKind.Absolute, out var a) || !Uri.TryCreate(url, UriKind.Absolute, out var b))
            return;
        if (!a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase))
            WarnOnce($"ВНИМАНИЕ: {source} указывает на {b.Host}, а сохранён сервер {a.Host}. Ключ и доступ к этому ПК получит {b.Host}. Если вы этого не делали — прервите запуск (Ctrl+C).");
    }

    private static void WarnOnce(string message)
    {
        if (_urlWarned)
            return;
        _urlWarned = true;
        try { Console.Error.WriteLine(message); }
        catch (IOException) { }
    }

    public string? GetKey()
    {
        if (Environment.GetEnvironmentVariable("CONEXY_KEY") is { Length: > 0 } fromEnv)
            return fromEnv;
        if (string.IsNullOrEmpty(ProtectedKey))
            return null;

        try
        {
            var raw = Convert.FromBase64String(ProtectedKey);
            if (OperatingSystem.IsWindows())
                raw = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void SetKey(string key)
    {
        var raw = Encoding.UTF8.GetBytes(key);
        if (OperatingSystem.IsWindows())
            raw = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
        ProtectedKey = Convert.ToBase64String(raw);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // UNIX_0600: создаём файл сразу с правами владельца, без окна «читаемо всеми» между записью и chmod.
        WriteOwnerOnly(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>UNIX_0600: запись файла с секретами — на Linux/macOS только чтение/запись владельцем (0600).</summary>
    public static void WriteOwnerOnly(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }
        if (File.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using (var stream = new FileStream(path, new FileStreamOptions
               {
                   Mode = FileMode.Create, Access = FileAccess.Write,
                   UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
               }))
        using (var writer = new StreamWriter(stream))
            writer.Write(content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
