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

/// <summary>Статус платежа для страницы возврата (поллинг до <c>succeeded</c>/<c>canceled</c>).</summary>
public record PaymentStatusResponse(
    Guid PaymentId,
    string Status,
    string PlanId,
    string Tier,
    int AmountRub);
