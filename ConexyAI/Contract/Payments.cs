namespace ConexyAI.Contract;

// YOOKASSA: добавлено 2026-09-27
/// <summary>Запрос на оплату тарифа: что именно покупает пользователь.</summary>
public record CreatePaymentRequest(string Plan);

/// <summary>Ответ на создание платежа: на <see cref="ConfirmationUrl"/> браузер и редиректит.</summary>
public record CreatePaymentResponse(
    Guid PaymentId,
    string Status,
    string ConfirmationUrl,
    int AmountRub,
    string PlanId);

/** Статус платежа для страницы возврата (поллинг до <c>succeeded</c>/<c>canceled</c>). */
public record PaymentStatusResponse(
    Guid PaymentId,
    string Status,
    string PlanId,
    string Tier,
    int AmountRub);

// YOOKASSA: добавлено 2026-09-27 — админская сводка оплат для ручных чеков в «Мой налог».
/// <summary>Одна строка сводки: дата, сумма, тариф и покупатель.</summary>
public record AdminPaymentSummaryItem(
    Guid PaymentId,
    DateTime PaidAt,
    int AmountRub,
    string PlanId,
    string Tier,
    Guid UserId,
    string? Email,
    string? Username);

/// <summary>
/// Сводка успешных платежей за период. <see cref="From"/>/<see cref="To"/> — границы периода в МСК
/// включительно (как их вводит админ), а не UTC.
/// </summary>
public record AdminPaymentSummaryResponse(
    DateOnly From,
    DateOnly To,
    int TotalRub,
    int Count,
    IReadOnlyList<AdminPaymentSummaryItem> Payments);
