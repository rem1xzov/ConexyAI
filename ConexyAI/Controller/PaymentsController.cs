using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ConexyAI.Controller;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Приём платежей: создание платежа, чтение статуса и вебхук от ЮKassa.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _payments;
    private readonly YooKassaSettings _settings;
    private readonly ILogger<PaymentsController> _logger;

    // Тело вебхука читается целиком, поэтому ограничиваем его размер.
    private const int MaxWebhookBodyBytes = 64 * 1024;

    public PaymentsController(
        IPaymentService payments,
        IOptions<YooKassaSettings> settings,
        ILogger<PaymentsController> logger)
    {
        _payments = payments;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Создаёт платёж и отдаёт ссылку, на которую нужно редиректить пользователя.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatePaymentResponse>> Create(
        [FromBody] CreatePaymentRequest? request,
        CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (request is null || string.IsNullOrWhiteSpace(request.Plan))
            return BadRequest(new { error = "PLAN_REQUIRED" });

        try
        {
            return Ok(await _payments.CreateAsync(userId, request.Plan, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = "UNKNOWN_PLAN", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Платёжный провайдер не настроен (нет shopId/секрета) — это ошибка конфигурации сервера.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "PAYMENTS_DISABLED", message = ex.Message });
        }
        catch (YooKassaException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "PAYMENT_PROVIDER_ERROR", message = ex.Message });
        }
    }

    /// <summary>Статус своего платежа — для страницы возврата после оплаты.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PaymentStatusResponse>> Status(Guid id, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var status = await _payments.GetStatusAsync(userId, id, ct);
        return status is null ? NotFound(new { error = "NOT_FOUND" }) : Ok(status);
    }

    /// <summary>
    /// Вебхук ЮKassa. Отвечает 200 на всё, что успешно разобрано (иначе ЮKassa будет повторять
    /// доставку), и 403 — если запрос пришёл не с адресов ЮKassa.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        var ipAllowed = _settings.IsNotificationIpAllowed(remoteIp);

        // Читаем тело ДО проверки, чтобы залогировать содержимое даже у отклонённого запроса
        // (это помогает ловить и подделки, и сдвиг подсетей ЮKassa).
        var raw = await ReadBodyAsync(ct);
        _logger.LogInformation(
            "YooKassa webhook received from {Ip} (allowed={Allowed}): {Body}",
            remoteIp?.ToString() ?? "unknown", ipAllowed, raw);

        if (!ipAllowed)
        {
            _logger.LogWarning("YooKassa webhook rejected: source {Ip} is not in the allowed ranges.", remoteIp);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "IP_NOT_ALLOWED" });
        }

        if (string.IsNullOrWhiteSpace(raw))
            return BadRequest(new { error = "EMPTY_BODY" });

        string eventName;
        string providerPaymentId;
        string providerStatus;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            eventName = root.TryGetProperty("event", out var eventEl) ? eventEl.GetString() ?? string.Empty : string.Empty;
            if (!root.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.Object)
                return BadRequest(new { error = "OBJECT_MISSING" });

            providerPaymentId = obj.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            providerStatus = obj.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            _logger.LogWarning("YooKassa webhook: malformed JSON body.");
            return BadRequest(new { error = "MALFORMED_JSON" });
        }

        if (string.IsNullOrEmpty(providerPaymentId))
            return BadRequest(new { error = "PAYMENT_ID_MISSING" });

        try
        {
            await _payments.HandleNotificationAsync(eventName, providerPaymentId, providerStatus, ct);
        }
        catch (YooKassaException ex)
        {
            // Не смогли подтвердить платёж через API — пусть ЮKassa повторит доставку позже.
            _logger.LogError(ex, "YooKassa webhook: could not verify payment {ProviderId}.", providerPaymentId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "VERIFICATION_FAILED" });
        }

        return Ok(new { received = true });
    }

    private async Task<string> ReadBodyAsync(CancellationToken ct)
    {
        // RequestSizeLimit здесь не поставить (нет атрибута на action), поэтому читаем с ограничением.
        var buffer = new byte[MaxWebhookBodyBytes];
        var total = 0;
        int read;
        while (total < buffer.Length &&
               (read = await Request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
