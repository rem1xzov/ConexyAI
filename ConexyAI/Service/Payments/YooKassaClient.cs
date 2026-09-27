using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConexyAI.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service.Payments;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Клиент API ЮKassa (v3). Авторизация — Basic с shopId и секретным ключом (ключ НЕ логируется),
/// на создание платежа обязателен заголовок <c>Idempotence-Key</c>: повтор запроса с тем же ключом
/// не создаёт второй платёж, а возвращает первый — это и защищает от двойного списания при ретрае.
/// </summary>
public class YooKassaClient : IYooKassaClient
{
    private readonly HttpClient _httpClient;
    private readonly YooKassaSettings _settings;
    private readonly ILogger<YooKassaClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Ограничиваем то, что попадает в лог из чужого ответа.
    private const int MaxErrorBodyChars = 800;

    public YooKassaClient(
        HttpClient httpClient,
        IOptions<YooKassaSettings> settings,
        ILogger<YooKassaClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public async Task<YooKassaPayment> CreatePaymentAsync(
        YooKassaPaymentRequest request,
        string idempotenceKey,
        CancellationToken ct = default)
    {
        EnsureConfigured();

        // ЮKassa принимает сумму строкой с двумя знаками после точки: "990.00".
        var value = request.AmountRub.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        var body = new Dictionary<string, object?>
        {
            ["amount"] = new { value, currency = "RUB" },
            ["capture"] = true,
            ["description"] = Truncate(request.Description, 128),
            ["confirmation"] = new { type = "redirect", return_url = request.ReturnUrl },
            ["metadata"] = request.Metadata,
        };

        // Чек для 54-ФЗ: ЮKassa требует его для большинства магазинов. Если email у пользователя нет,
        // чек не отправляем — платёж без чека магазин может отклонить, и это будет видно в логах.
        if (!string.IsNullOrWhiteSpace(request.CustomerEmail))
        {
            body["receipt"] = new
            {
                customer = new { email = request.CustomerEmail },
                items = new object[]
                {
                    new
                    {
                        description = Truncate(request.Description, 128),
                        quantity = "1.00",
                        amount = new { value, currency = "RUB" },
                        // vat_code 1 — «без НДС» (исполнитель на НПД/УСН без НДС).
                        vat_code = 1,
                        payment_subject = "service",
                        payment_mode = "full_payment",
                    },
                },
            };
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl()}/payments")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = BasicAuthHeader();
        message.Headers.TryAddWithoutValidation("Idempotence-Key", idempotenceKey);

        _logger.LogInformation("YooKassa: creating payment for {Amount} RUB (idempotence {Key})", value, idempotenceKey);

        using var response = await _httpClient.SendAsync(message, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Секретный ключ сюда не попадает: тело ошибки — ответ ЮKassa, не наш запрос.
            _logger.LogError("YooKassa create payment failed [{Status}]: {Body}", (int)response.StatusCode, Truncate(payload, MaxErrorBodyChars));
            throw new YooKassaException($"ЮKassa отклонила создание платежа ({(int)response.StatusCode}). {Truncate(payload, MaxErrorBodyChars)}");
        }

        return ParsePayment(payload);
    }

    public async Task<YooKassaPayment?> GetPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        EnsureConfigured();

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl()}/payments/{Uri.EscapeDataString(paymentId)}");
        message.Headers.Authorization = BasicAuthHeader();

        using var response = await _httpClient.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("YooKassa get payment failed [{Status}]: {Body}", (int)response.StatusCode, Truncate(payload, MaxErrorBodyChars));
            throw new YooKassaException($"Не удалось прочитать платёж в ЮKassa ({(int)response.StatusCode}).");
        }

        return ParsePayment(payload);
    }

    private void EnsureConfigured()
    {
        if (!_settings.IsConfigured)
            throw new YooKassaException("ЮKassa не настроена: задайте YooKassa__ShopId и YooKassa__SecretKey.");
    }

    private string BaseUrl() => _settings.ApiBaseUrl.TrimEnd('/');

    private AuthenticationHeaderValue BasicAuthHeader()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.ShopId}:{_settings.SecretKey}"));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    private static YooKassaPayment ParsePayment(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? string.Empty : string.Empty;

        string? confirmationUrl = null;
        if (root.TryGetProperty("confirmation", out var confirmation) &&
            confirmation.ValueKind == JsonValueKind.Object &&
            confirmation.TryGetProperty("confirmation_url", out var urlEl))
        {
            confirmationUrl = urlEl.GetString();
        }

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(status))
            throw new YooKassaException("ЮKassa вернула ответ без id/статуса платежа.");

        return new YooKassaPayment(id, status, confirmationUrl);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
