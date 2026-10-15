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

        if (Environment.GetEnvironmentVariable("CONEXY_URL") is { Length: > 0 } url)
            config.ServerUrl = url;
        return config;
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
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
