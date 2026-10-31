using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Tools;

/// <summary>
/// SECRET_MASK: маскирует похожие на секреты значения (токены GitHub/OpenAI/AWS/Slack, JWT, ключи ConexyAI,
/// «password=…», заголовки Authorization, длинный base64) перед записью в журнал.
/// Эвристика: лучше лишний раз скрыть, чем оставить токен в daemon.log.
/// </summary>
public static partial class SecretMask
{
    [GeneratedRegex(@"(?i)\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|cnx_[A-Za-z0-9_\-]{8,}|sk-[A-Za-z0-9_\-]{16,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9\-]{10,}|ya29\.[A-Za-z0-9_\-]{10,}|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})")]
    private static partial Regex KnownTokens();

    [GeneratedRegex(@"(?i)((?:token|secret|password|passwd|pwd|api[_-]?key|access[_-]?key|authorization|bearer|basic)\s*[:=]?\s*[""']?)([^\s""'&]{6,})")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"[A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex LongBase64();

    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        text = KnownTokens().Replace(text, "***");
        text = KeyValue().Replace(text, m => m.Groups[1].Value + "***");
        return LongBase64().Replace(text, "***");
    }
}
