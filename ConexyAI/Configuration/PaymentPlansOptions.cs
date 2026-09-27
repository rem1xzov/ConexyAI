namespace ConexyAI.Configuration;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Каталог тарифов, которые можно купить. Привязка «кнопка → цена» живёт ЗДЕСЬ, на сервере, а не на
/// фронтенде: сумму платежа нельзя позволить прислать клиенту, иначе её можно подделать. Фронтенд
/// показывает цены только для вида (см. UpgradeModal), а фактическую сумму берёт из этих настроек.
/// </summary>
public class PaymentPlansOptions
{
    public const string SectionName = "PaymentPlans";

    /// <summary>Каталог: ключ — идентификатор плана (Pro, ProMaxAnnual, …).</summary>
    public Dictionary<string, PaymentPlan> Plans { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>План по идентификатору или <c>null</c>, если такого нет.</summary>
    public PaymentPlan? Find(string? planId) =>
        !string.IsNullOrWhiteSpace(planId) && Plans.TryGetValue(planId.Trim(), out var plan) ? plan : null;
}

/// <summary>Один покупаемый тариф: сколько списать, что выдать и на сколько.</summary>
public class PaymentPlan
{
    /// <summary>Выдаваемый тариф. Должен совпадать с именем <see cref="Model.SubscriptionTier"/> (Go/Pro/ProMax).</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>Сумма к списанию в рублях (целое число рублей).</summary>
    public int AmountRub { get; set; }

    /// <summary>На сколько месяцев выдаётся доступ. Годовой тариф — 12.</summary>
    public int Months { get; set; } = 1;

    /// <summary>Название для описания платежа (в чеке и в интерфейсе ЮKassa).</summary>
    public string Title { get; set; } = string.Empty;
}
