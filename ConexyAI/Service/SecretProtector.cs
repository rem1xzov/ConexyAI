using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// USER_INTEGRATIONS: добавлено 2026-10-10 — шифрование персональных секретов (GitHub PAT, MCP-токены)
// перед записью в БД. AES-GCM даёт и конфиденциальность, и защиту от подмены (тег аутентификации).
// Ключ берём из окружения: SECRETS_KEY, а если его нет — из обязательного Jwt:SigningKey, чтобы фича
// работала без новой переменной и переживала перезапуск контейнера.
public interface ISecretProtector
{
    /// <summary>Шифрует текст; пустое значение превращается в null. Возвращает base64.</summary>
    string? Protect(string? plaintext);

    /// <summary>Расшифровывает значение; null при пустом/повреждённом/чужом ключе.</summary>
    string? Unprotect(string? ciphertext);
}

public class AesSecretProtector : ISecretProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;
    private readonly ILogger<AesSecretProtector> _logger;

    public AesSecretProtector(IConfiguration configuration, ILogger<AesSecretProtector> logger)
    {
        _logger = logger;
        var secret = configuration["SECRETS_KEY"];
        if (string.IsNullOrWhiteSpace(secret))
            secret = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "Neither SECRETS_KEY nor Jwt:SigningKey is configured, so user secrets cannot be protected.");

        // SHA-256 нормализует любой секрет до ровно 32 байт ключа.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return null;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var output = new byte[1 + NonceSize + TagSize + cipher.Length];
        output[0] = Version;
        Buffer.BlockCopy(nonce, 0, output, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, output, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, 1 + NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(output);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
            return null;

        try
        {
            var data = Convert.FromBase64String(ciphertext);
            if (data.Length < 1 + NonceSize + TagSize || data[0] != Version)
                return null;

            var nonce = data.AsSpan(1, NonceSize);
            var tag = data.AsSpan(1 + NonceSize, TagSize);
            var cipher = data.AsSpan(1 + NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // Другой ключ или испорченные данные: считаем, что секрета нет, а не роняем запрос.
            _logger.LogWarning("Could not decrypt a stored user secret; treating it as absent.");
            return null;
        }
    }
}
