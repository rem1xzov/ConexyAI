using ConexyAI.Model;

namespace ConexyAI.Service;

// ORCHESTRA: добавлено 2026-09-28
/// <summary>
/// Кому доступен «Оркестр агентов» — несколько агентов-помощников на одну задачу.
/// <para>
/// Только тариф ProMax (администратор — как ProMax) и только режим Coder. У Cowork всегда один агент:
/// это исследовательский режим, где параллельные помощники ничего не добавляют к качеству, зато
/// удваивают расход токенов.
/// </para>
/// <para>
/// Проверка живёт на сервере и НЕ доверяет клиенту: флаг из запроса — только пожелание, окончательное
/// решение принимает <c>ConexyBackgroundWorker</c> по фактическому тарифу пользователя.
/// </para>
/// </summary>
public static class OrchestraEligibility
{
    /// <summary>Минимальный тариф, на котором доступен оркестр.</summary>
    public const string RequiredTier = "ProMax";

    public static bool IsAllowed(string? tier, bool isAdmin, ConexyModelType modelType)
    {
        if (modelType != ConexyModelType.ConexyCoder) return false;
        if (isAdmin) return true;
        // ANNUAL_ULTRA: годовой Ultra выше ProMax, поэтому оркестр ему тоже доступен.
        return string.Equals(tier, RequiredTier, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tier, "Ultra", StringComparison.OrdinalIgnoreCase);
    }
}
