namespace ConexyAI.Service.Payments;

// YOOKASSA: добавлено 2026-09-27
/// <summary>Платёж, как его отдаёт API ЮKassa (только нужные нам поля).</summary>
public record YooKassaPayment(string Id, string Status, string? ConfirmationUrl);

/// <summary>Данные для создания платежа.</summary>
public record YooKassaPaymentRequest(
    int AmountRub,
    string Description,
    string ReturnUrl,
    IReadOnlyDictionary<string, string> Metadata,
    string? CustomerEmail);

/// <summary>ЮKassa ответила ошибкой — наверх уходит статус и урезанное тело ответа.</summary>
public class YooKassaException : Exception
{
    public YooKassaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Тонкая обёртка над API ЮKassa (v3/payments). Знает про Basic Auth, Idempotence-Key и разбор
/// ответа; ничего не знает про тарифы и пользователей — это дело <see cref="IPaymentService"/>.
/// </summary>
public interface IYooKassaClient
{
    /// <summary>Настроены ли shopId и секретный ключ.</summary>
    bool IsConfigured { get; }

    /// <summary>Создаёт платёж и возвращает его id и ссылку на оплату.</summary>
    Task<YooKassaPayment> CreatePaymentAsync(
        YooKassaPaymentRequest request,
        string idempotenceKey,
        CancellationToken ct = default);

    /// <summary>Читает текущее состояние платежа по его id в ЮKassa. <c>null</c> — не найден.</summary>
    Task<YooKassaPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default);
}
