using System.Security.Cryptography;
using System.Text;

namespace ConexyAI.Desktop.Mcp;

/// <summary>
/// Шифрование секретов коннекторов (токены MCP, пароли почты) для текущего пользователя Windows (DPAPI):
/// файл конфигурации можно скопировать, но расшифровать его на другом ПК или под другим пользователем нельзя.
/// </summary>
public static class SecretBox
{
    public static string Protect(string value)
    {
        var raw = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
            raw = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(raw);
    }

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            var raw = Convert.FromBase64String(value);
            if (OperatingSystem.IsWindows())
                raw = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
