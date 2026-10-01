namespace ConexyAI.Entity;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Локальная запись о платеже. Нужна, чтобы: связать возврат пользователя на сайт с платежом
/// (по нашему id, а не по чужому), показать статус при поллинге, и не выдать тариф дважды, если
/// ЮKassa повторит уведомление (вебхуки доставляются с повторами).
/// </summary>
public class PaymentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>Идентификатор плана из каталога (Pro, ProMaxAnnual, …).</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>Выдаваемый тариф (Go/Pro/ProMax/Ultra) — снимок на момент покупки.</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>На сколько месяцев выдаётся доступ (из каталога планов).</summary>
    public int Months { get; set; } = 1;

    /// <summary>Сумма в рублях.</summary>
    public int AmountRub { get; set; }

    /// <summary>Идентификатор платежа в ЮKassa (<c>id</c> из ответа API).</summary>
    public string ProviderPaymentId { get; set; } = string.Empty;

    /// <summary>Статус ЮKassa: <c>pending</c> | <c>waiting_for_capture</c> | <c>succeeded</c> | <c>canceled</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Idempotence-Key, с которым создавался платёж (для разбора спорных случаев).</summary>
    public string IdempotenceKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Когда ЮKassa сообщила об успехе.</summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>Когда тариф был фактически выдан. Защита от повторной выдачи по тому же платежу.</summary>
    public DateTime? ActivatedAt { get; set; }
}
