using ConexyAI.Contract;

namespace ConexyAI.Service;

/// <summary>
/// TOKEN_METER: предварительная оценка токенов до отправки запроса.
/// Перенесено из deepseek-harness (<c>packages/llm/token-meter/src/estimate.ts:13-19</c>):
/// 4 символа = 1 токен плюс 4 токена накладных расходов на сообщение/блок.
/// Нужна, чтобы резать контекст ДО запроса, а не получать отказ провайдера уже после отправки.
/// </summary>
public static class TokenMeter
{
    /// <summary>Сколько символов приходится на один токен (оценка harness).</summary>
    public const int CharsPerToken = 4;

    /// <summary>Накладные расходы на одно сообщение: роль, разделители.</summary>
    public const int PerMessageOverhead = 4;

    /// <summary>Оценка токенов для произвольного текста.</summary>
    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        return (text.Length + CharsPerToken - 1) / CharsPerToken;
    }

    /// <summary>Оценка токенов одного сообщения вместе с его аргументами вызова инструмента.</summary>
    public static int Estimate(ChatMessage message)
    {
        var tokens = PerMessageOverhead + EstimateText(message.Text);
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                tokens += PerMessageOverhead;
                tokens += EstimateText(call.Function?.Name);
                tokens += EstimateText(call.Function?.Arguments);
            }
        }
        return tokens;
    }

    /// <summary>Оценка токенов всей истории.</summary>
    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

    /// <summary>Пора сжимать: история занимает больше <paramref name="ratio"/> окна модели.</summary>
    public static bool NeedsCompaction(IEnumerable<ChatMessage> messages, int contextWindow, double ratio = 0.8) =>
        contextWindow > 0 && Estimate(messages) > contextWindow * ratio;
}
