using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConexyAI.Desktop;

/// <summary>
/// UPDATE_MIRROR: обновления можно раздавать со своего домена. Манифест latest.json подписывается
/// ECDSA P-256; клиент проверяет подпись встроенным публичным ключом. Тогда компрометация сервера
/// или зеркала не позволяет подсунуть чужой exe — в отличие от одной лишь SHA-256, которую сервер
/// мог бы подменить вместе с файлом.
/// </summary>
public static class UpdateSigning
{
    /// <summary>Публичный ключ подписи манифеста. Приватный — .update-signing-key.pem (вне git).</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvjQmJf7EJdGNiLZChl2rvS0v7mxH
        fpi6jTfnsxnbIM/SCo+vP/viYtEcU2ssc27aYlSuRydhnC1C+JyC8iWAmg==
        -----END PUBLIC KEY-----
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public sealed class Manifest
    {
        public string Version { get; set; } = string.Empty;
        public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Signature { get; set; }
    }

    public sealed class FileEntry
    {
        public string Url { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    /// <summary>Каноническая строка, которую подписываем. Одинакова в клиенте и в команде updatesign.</summary>
    public static byte[] Canonical(Manifest manifest)
    {
        var builder = new StringBuilder("conexy-update-v1\n").Append(manifest.Version?.Trim() ?? string.Empty).Append('\n');
        foreach (var (rid, file) in (manifest.Files ?? []).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(rid.ToLowerInvariant()).Append('|')
                .Append(file.Url.Trim()).Append('|')
                .Append(file.Sha256.Trim().ToLowerInvariant()).Append('|')
                .Append(file.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static bool Verify(Manifest manifest) => Verify(manifest, PublicKeyPem);

    public static bool Verify(Manifest manifest, string publicKeyPem)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Signature)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Files is not { Count: > 0 })
            return false;
        try
        {
            var signature = Convert.FromBase64String(manifest.Signature);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(Canonical(manifest), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static string Sign(Manifest manifest, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(ecdsa.SignData(Canonical(manifest), HashAlgorithmName.SHA256));
    }

    public static Manifest? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Manifest>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(Manifest manifest) => JsonSerializer.Serialize(manifest, Json);

    /// <summary>RID текущего процесса: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64.</summary>
    public static string CurrentRid
    {
        get
        {
            var arm = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                      == System.Runtime.InteropServices.Architecture.Arm64;
            if (OperatingSystem.IsWindows())
                return arm ? "win-arm64" : "win-x64";
            if (OperatingSystem.IsMacOS())
                return arm ? "osx-arm64" : "osx-x64";
            return arm ? "linux-arm64" : "linux-x64";
        }
    }

    /// <summary>`conexy updateverify latest.json` — проверить подпись манифеста встроенным ключом.</summary>
    public static int VerifyCommand(string[] args)
    {
        var path = args.SkipWhile(a => !a.Equals("updateverify", StringComparison.OrdinalIgnoreCase)).Skip(1)
            .FirstOrDefault(a => !a.StartsWith("--"));
        var file = path is null ? null : (Directory.Exists(path) ? Path.Combine(path, "latest.json") : Path.GetFullPath(path));
        if (file is null || !File.Exists(file))
        {
            Console.Error.WriteLine("Использование: conexy updateverify <latest.json|папка>");
            return 64;
        }
        var manifest = Parse(File.ReadAllText(file));
        if (manifest is null)
        {
            Console.Error.WriteLine("Не JSON-манифест: " + file);
            return 1;
        }
        var ok = Verify(manifest);
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  подпись манифеста v{manifest.Version}");
        foreach (var (rid, entry) in (manifest.Files ?? []).OrderBy(f => f.Key))
            Console.WriteLine($"  {rid,-12} {entry.Size,12:N0}  {entry.Sha256[..Math.Min(16, entry.Sha256.Length)]}…  {entry.Url}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// `conexy updatesign <папка> <версия> [--url-base <адрес>] [--key <файл>]` — собрать latest.json
    /// по файлам conexy-&lt;rid&gt;[.exe] и подписать его.
    /// </summary>
    public static int Command(string[] args)
    {
        var rest = args.SkipWhile(a => !a.Equals("updatesign", StringComparison.OrdinalIgnoreCase)).Skip(1)
            .Where(a => !a.StartsWith("--")).ToArray();
        if (rest.Length < 2)
        {
            Console.Error.WriteLine("Использование: conexy updatesign <папка> <версия> [--url-base https://conexyai.ru/downloads] [--key .update-signing-key.pem]");
            return 64;
        }

        var dir = Path.GetFullPath(rest[0]);
        var version = rest[1].Trim();
        var urlBase = (Option(args, "--url-base") ?? "https://conexyai.ru/downloads").TrimEnd('/');
        var keyPath = Option(args, "--key") ?? Path.Combine(Directory.GetCurrentDirectory(), ".update-signing-key.pem");
        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine("Нет приватного ключа: " + keyPath);
            return 66;
        }

        var manifest = new Manifest { Version = version, PublishedAt = DateTimeOffset.UtcNow };
        var knownRids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64",
        };
        foreach (var path in Directory.EnumerateFiles(dir, "conexy-*")
                     .Where(p => !Path.GetFileName(p).StartsWith("conexy-setup", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".new", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".old", StringComparison.OrdinalIgnoreCase)
                || name.Equals("latest.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var rid = name["conexy-".Length..];
            if (rid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                rid = rid[..^4];
            if (!knownRids.Contains(rid))
                continue;
            using var stream = File.OpenRead(path);
            manifest.Files[rid.ToLowerInvariant()] = new FileEntry
            {
                Url = $"{urlBase}/{Uri.EscapeDataString(name)}",
                Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                Size = stream.Length,
            };
        }

        if (manifest.Files.Count == 0)
        {
            Console.Error.WriteLine("В папке нет файлов conexy-<rid>[.exe]: " + dir);
            return 66;
        }

        manifest.Signature = Sign(manifest, File.ReadAllText(keyPath));
        var target = Path.Combine(dir, "latest.json");
        File.WriteAllText(target, Serialize(manifest), new UTF8Encoding(false));
        Console.WriteLine("Манифест: " + target);
        foreach (var (rid, file) in manifest.Files)
            Console.WriteLine($"  {rid,-12} {file.Size,12:N0}  {file.Sha256[..16]}…  {file.Url}");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
