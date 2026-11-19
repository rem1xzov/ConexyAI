using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConexyAI.Desktop.Tools;

// LOCAL_WORKER: добавлено 2026-11-19 — локальная модель, установленная на ПК пользователя. Сервер
// присылает тело обычного chat.completion запроса; клиент переписывает его под локальный сервер
// (stream=false, имя модели из настроек), отправляет на 127.0.0.1 и возвращает JSON-ответ как есть.
public sealed class LocalModelClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<ToolResult> ChatAsync(ToolArgs args, CancellationToken ct)
    {
        var body = args.Str("body");
        if (string.IsNullOrWhiteSpace(body))
            return ToolResult.Fail("local_chat: пустое тело запроса.");

        var settings = ClientConfig.LoadLocalModel();
        if (!settings.Enabled)
            return ToolResult.Fail("Локальная модель отключена в настройках клиента. Включите её командой `/local on`.");

        try
        {
            var prepared = PrepareBody(body, settings.Model);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}/chat/completions")
            {
                Content = new StringContent(prepared, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            using var response = await Http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return ToolResult.Fail(
                    $"Локальная модель вернула {(int)response.StatusCode}: {Trim(text, 800)}. " +
                    "Проверьте `/local` — запущен ли Ollama/LM Studio и загружена ли модель.");
            }

            return ToolResult.Ok(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ToolResult.Fail(
                $"Не удалось связаться с локальной моделью по адресу {settings.Url}: {ex.Message}. " +
                "Проверьте, что Ollama / LM Studio запущены и адрес верный (`/local`).");
        }
    }

    /// <summary>Проверка связи с локальным сервером (`/local test`): список моделей и наличие настроенной.</summary>
    public static async Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken ct = default)
    {
        var settings = ClientConfig.LoadLocalModel();
        if (!settings.Enabled)
            return (false, "Локальная модель отключена. Включите: `/local on`.");
        try
        {
            using var response = await Http.GetAsync($"{settings.Url}/models", ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (false, $"Локальный сервер ответил {(int)response.StatusCode}: {Trim(text, 400)}");
            var present = text.Contains(settings.Model, StringComparison.OrdinalIgnoreCase);
            return (true, present
                ? $"Локальная модель доступна: {settings.Model} @ {settings.Url}."
                : $"Сервер по адресу {settings.Url} отвечает, но модели «{settings.Model}» в списке нет — смените имя: `/local model …`.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"Нет связи с локальной моделью по адресу {settings.Url}: {ex.Message}. Запустите Ollama / LM Studio.");
        }
    }

    /// <summary>
    /// Готовит тело запроса под локальный сервер: `stream=false`, без `stream_options` и с именем
    /// модели из настроек ПК. Невалидное тело возвращается как есть — ошибку покажет сам сервер.
    /// </summary>
    public static string PrepareBody(string body, string? model)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
                return body;

            root["stream"] = false;
            root.Remove("stream_options");
            root.Remove("thinking");
            root.Remove("reasoning_effort");
            if (!string.IsNullOrWhiteSpace(model))
                root["model"] = model;
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
