using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConexyAI.Desktop.Tools;

// WEB_VERIFY: добавлено 2026-11-21 — проверка работоспособности сайта БЕЗ computer use: открываем
// страницу в настоящем headless Edge/Chrome (CDP), кликаем кнопки, заполняем поля, нажимаем Enter и
// проверяем, что DOM действительно изменился (текст, количество карточек, значения, URL), плюс собираем
// ошибки JS. Скриншот показывает «красиво», а этот инструмент — «работает ли».
public sealed class WebVerifyTool
{
    private const int MaxSteps = 15;
    private const int DefaultTimeoutSeconds = 60;
    private const int MaxTimeoutSeconds = 180;
    private const int SettleDelayMs = 300;
    private const int StepDelayMs = 120;
    private const int PageReadyTimeoutSeconds = 20;

    private static readonly JsonSerializerOptions JsString = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // INIT_SCRIPT: ошибки JS собираем с самого начала загрузки, а не только после навигации.
    private const string ErrorCaptureScript = """
        window.__webVerifyErrors = [];
        window.addEventListener('error', function (e) {
          try { window.__webVerifyErrors.push(String((e && (e.message || e.error)) || e)); } catch (_) {}
        });
        window.addEventListener('unhandledrejection', function (e) {
          try { window.__webVerifyErrors.push('unhandledrejection: ' + String(e && e.reason)); } catch (_) {}
        });
        """;

    public sealed record Step(string Action, string? Selector, string? Value, string? Text, int? Count, string? Key);

    public async Task<ToolResult> RunAsync(ToolArgs args, CancellationToken ct)
    {
        var url = args.Required("url", "web_verify").Trim();
        if (!IsSupportedUrl(url))
            return ToolResult.Fail("web_verify: url должен быть http://, https:// или file://. Для локального сайта сначала запусти сервер (bg_start) и передай http://localhost:<порт>.");

        var parsed = ParseSteps(args.Raw("steps"));
        if (parsed.Error is not null)
            return ToolResult.Fail("web_verify: " + parsed.Error);
        var steps = parsed.Steps;

        var timeoutSeconds = Math.Clamp(args.Int("timeout_seconds") ?? DefaultTimeoutSeconds, 5, MaxTimeoutSeconds);
        var browser = PcTools.FindBrowser();
        if (browser is null)
            return ToolResult.Fail("web_verify: на ПК не найден Edge/Chrome. Установи браузер (install_software Google.Chrome) или проверь страницу через http_request/page_screenshot.");

        var stage = "connect";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        // PROFILE_DIR: профиль браузера держим в LOCALAPPDATA, а не в TEMP процесса: если TEMP указывает
        // внутрь рабочей папки (сборка/CI), Edge/Chrome не отдают CDP-ответы. Пользовательская папка работает всегда.
        var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "conexy-webverify");
        Directory.CreateDirectory(profileRoot);
        var temp = Path.Combine(profileRoot, "run-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var port = GetFreePort();
        var psi = new ProcessStartInfo(browser)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--remote-allow-origins=*",
                     "--no-default-browser-check", "--disable-background-networking",
                     "--remote-debugging-port=" + port, "--user-data-dir=" + temp,
                     "--window-size=1280,900", "about:blank",
                 })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return ToolResult.Fail("web_verify: не удалось запустить headless-браузер.");
            await using var cdp = await CdpClient.ConnectAsync(port, cts.Token);
            stage = "navigate";
            await cdp.SendAsync("Page.enable", null, cts.Token);
            await cdp.SendAsync("Runtime.enable", null, cts.Token);
            await cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                new { source = ErrorCaptureScript }, cts.Token);
            await cdp.SendAsync("Page.navigate", new { url }, cts.Token);
            stage = "ready";
            if (!await WaitForReadyAsync(cdp, cts.Token))
                return ToolResult.Fail($"web_verify: страница не загрузилась за {PageReadyTimeoutSeconds} с: {url}. Проверь, что сервер запущен и отвечает.");
            await Task.Delay(SettleDelayMs, cts.Token);

            stage = "steps";
            var results = new List<string>();
            var failures = 0;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var outcome = await ExecuteStepAsync(cdp, step, cts.Token);
                var label = Describe(step);
                results.Add($"{i + 1}. {label} -> {(outcome.Ok ? "ok" : "FAIL")}{(outcome.Detail is null ? string.Empty : " (" + outcome.Detail + ")")}");
                if (!outcome.Ok)
                    failures++;
                await Task.Delay(StepDelayMs, cts.Token);
            }

            var consoleErrors = await ReadErrorsAsync(cdp, cts.Token);
            var title = await TryEvaluateStringAsync(cdp, "document.title", cts.Token);
            var finalUrl = await TryEvaluateStringAsync(cdp, "location.href", cts.Token);

            var text = new StringBuilder();
            text.AppendLine($"web_verify: {(failures == 0 ? "PASS" : "FAIL")} — шагов {steps.Count}, провалов {failures}");
            text.AppendLine($"url: {finalUrl ?? url}");
            text.AppendLine($"title: {title ?? "(нет)"}");
            foreach (var line in results)
                text.AppendLine(line);
            text.AppendLine(consoleErrors.Count == 0
                ? "ошибки JS: нет"
                : "ошибки JS:\n" + string.Join("\n", consoleErrors.Take(8).Select(error => "  - " + error)));
            if (failures > 0)
                text.AppendLine("Подсказка: посмотри, какой селектор/текст ожидался, и поправь разметку или обработчик. " +
                                "Для визуальной проверки можно дополнительно снять page_screenshot.");
            return failures == 0 ? ToolResult.Ok(text.ToString().TrimEnd()) : ToolResult.Fail(text.ToString().TrimEnd());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Fail($"web_verify: таймаут {timeoutSeconds} с (этап: {stage}). Упрости шаги или проверь, что сервер отвечает (http_request).");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or InvalidOperationException or JsonException)
        {
            return ToolResult.Fail("web_verify: браузер не смог проверить страницу: " + ex.Message);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsSupportedUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForReadyAsync(CdpClient cdp, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(PageReadyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await TryEvaluateStringAsync(cdp, "document.readyState", ct);
            if (state == "complete")
                return true;
            await Task.Delay(150, ct);
        }

        return false;
    }

    /// <summary>Собирает ошибки JS из init-скрипта и CDP-событий.</summary>
    private static async Task<List<string>> ReadErrorsAsync(CdpClient cdp, CancellationToken ct)
    {
        var errors = new List<string>();
        var captured = await TryEvaluateAsync(cdp, "window.__webVerifyErrors || []", ct);
        if (captured is { } element && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && !errors.Contains(text))
                    errors.Add(text);
            }
        }

        foreach (var runtimeError in cdp.RuntimeErrors)
        {
            if (!errors.Contains(runtimeError))
                errors.Add(runtimeError);
        }

        return errors;
    }

    private static async Task<string?> TryEvaluateStringAsync(CdpClient cdp, string expression, CancellationToken ct) =>
        (await TryEvaluateAsync(cdp, expression, ct)) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static async Task<JsonElement?> TryEvaluateAsync(CdpClient cdp, string expression, CancellationToken ct)
    {
        try
        {
            return await cdp.EvaluateAsync(expression, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }



    private static async Task<StepOutcome> ExecuteStepAsync(CdpClient cdp, Step step, CancellationToken ct)
    {
        if (step.Action == "press")
        {
            if (step.Selector is { Length: > 0 })
            {
                var focus = await EvaluateJsonAsync(cdp,
                    $"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(step.Selector, JsString)}); " +
                    "if (!el) return { ok: false, error: 'element not found' }; el.focus(); return { ok: true }; })()", ct);
                if (!focus.Ok)
                    return focus;
            }

            return await cdp.PressKeyAsync(step.Key ?? "Enter", ct);
        }

        var expression = BuildExpression(step);
        return await EvaluateJsonAsync(cdp, expression, ct);
    }

    /// <summary>JS-выражение шага; возвращает {ok, error, actual}. Вынесено отдельно — покрыто тестами.</summary>
    public static string BuildExpression(Step step)
    {
        var selector = JsonSerializer.Serialize(step.Selector ?? "body", JsString);
        var value = JsonSerializer.Serialize(step.Value ?? string.Empty, JsString);
        var text = JsonSerializer.Serialize(step.Text ?? string.Empty, JsString);
        return step.Action switch
        {
            "click" =>
                $"(() => {{ const el = document.querySelector({selector}); " +
                "if (!el) return { ok: false, error: 'element not found' }; el.scrollIntoView({ block: 'center' }); el.click(); return { ok: true }; })()",
            "fill" =>
                $"(() => {{ const el = document.querySelector({selector}); if (!el) return {{ ok: false, error: 'element not found' }}; " +
                "const proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype; " +
                $"const setter = Object.getOwnPropertyDescriptor(proto, 'value').set; setter.call(el, {value}); " +
                "el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return { ok: true }; })()",
            "expect_text" =>
                $"(() => {{ const el = document.querySelector({selector}); const actual = ((el && el.innerText) || '').replace(/\\s+/g, ' ').trim(); " +
                $"const wanted = {text}; return {{ ok: actual.includes(wanted), error: actual.includes(wanted) ? null : 'text not found', actual: actual.slice(0, 300) }}; }})()",
            "expect_visible" =>
                $"(() => {{ const el = document.querySelector({selector}); const visible = !!el && (el.offsetWidth > 0 || el.offsetHeight > 0 || el.getClientRects().length > 0) && getComputedStyle(el).visibility !== 'hidden'; " +
                "return { ok: visible, error: visible ? null : (el ? 'element hidden' : 'element not found') }; })()",
            "expect_count" =>
                $"(() => {{ const actual = document.querySelectorAll({selector}).length; const wanted = {(step.Count ?? 1)}; " +
                "return { ok: actual === wanted, error: actual === wanted ? null : 'count mismatch', actual: actual }; })()",
            "expect_value" =>
                $"(() => {{ const el = document.querySelector({selector}); const actual = el ? el.value : null; " +
                $"return {{ ok: el !== null && actual === {value}, error: el === null ? 'element not found' : 'value mismatch', actual: actual }}; }})()",
            "expect_url" =>
                $"(() => {{ const actual = location.href; const wanted = {value}; return {{ ok: actual.includes(wanted), error: actual.includes(wanted) ? null : 'url mismatch', actual: actual }}; }})()",
            _ => "(() => ({ ok: false, error: 'unsupported action' }))()",
        };
    }

    private static async Task<StepOutcome> EvaluateJsonAsync(CdpClient cdp, string expression, CancellationToken ct)
    {
        var element = await cdp.EvaluateAsync(expression, ct);
        if (element is not { ValueKind: JsonValueKind.Object } obj)
            return new StepOutcome(false, "unexpected browser result");
        var ok = obj.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        string? detail = null;
        if (obj.TryGetProperty("actual", out var actual) && actual.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            detail = actual.ValueKind == JsonValueKind.String ? actual.GetString() : actual.GetRawText();
        if (!ok && obj.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            detail = error.GetString() + (detail is null ? string.Empty : " · actual=" + detail);
        return new StepOutcome(ok, detail);
    }

    private static string Describe(Step step) => step.Action switch
    {
        "click" => $"click {step.Selector}",
        "fill" => $"fill {step.Selector} = {step.Value}",
        "press" => $"press {step.Key ?? "Enter"}",
        "expect_text" => $"expect text «{step.Text}»" + (step.Selector is { Length: > 0 } selector && selector != "body" ? $" in {selector}" : string.Empty),
        "expect_visible" => $"expect visible {step.Selector}",
        "expect_count" => $"expect count {step.Selector} = {step.Count ?? 1}",
        "expect_value" => $"expect value {step.Selector} = {step.Value}",
        "expect_url" => $"expect url contains {step.Value}",
        _ => step.Action,
    };

    public sealed record ParseResult(List<Step> Steps, string? Error);

    public static ParseResult ParseSteps(JsonElement? raw)
    {
        var steps = new List<Step>();
        if (raw is not { ValueKind: JsonValueKind.Array } array)
            return new ParseResult(steps, null);
        if (array.GetArrayLength() > MaxSteps)
            return new ParseResult(steps, $"слишком много шагов (до {MaxSteps}).");
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return new ParseResult(steps, "каждый шаг — объект {action, selector?, value?, text?, count?, key?}.");
            var action = ReadString(item, "action")?.Trim().ToLowerInvariant();
            if (action is not ("click" or "fill" or "press" or "expect_text" or "expect_visible" or "expect_count" or "expect_value" or "expect_url"))
                return new ParseResult(steps, "неизвестное действие шага: " + action + ". Доступно: click, fill, press, expect_text, expect_visible, expect_count, expect_value, expect_url.");
            var selector = ReadString(item, "selector");
            if (action is "click" or "fill" or "expect_visible" or "expect_count" or "expect_value")
            {
                if (string.IsNullOrWhiteSpace(selector))
                    return new ParseResult(steps, $"для действия {action} нужен selector.");
            }
            if (action is "fill" or "expect_value" && !item.TryGetProperty("value", out _))
                return new ParseResult(steps, $"для действия {action} нужно value.");
            if (action == "expect_text" && string.IsNullOrWhiteSpace(ReadString(item, "text")))
                return new ParseResult(steps, "для expect_text нужен text.");
            if (action == "expect_count" && !item.TryGetProperty("count", out _))
                return new ParseResult(steps, "для expect_count нужен count.");
            steps.Add(new Step(
                action,
                selector,
                ReadString(item, "value"),
                ReadString(item, "text"),
                item.TryGetProperty("count", out var count) && count.TryGetInt32(out var n) ? n : null,
                ReadString(item, "key")));
        }

        return new ParseResult(steps, null);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Минимальный CDP-клиент: команды по WebSocket, Runtime.evaluate и Input.dispatchKeyEvent.</summary>
public sealed record StepOutcome(bool Ok, string? Detail);


internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<string> _runtimeErrors = new();
    private int _nextId;

    private CdpClient(ClientWebSocket socket) => _socket = socket;

    public IReadOnlyList<string> RuntimeErrors => _runtimeErrors;

    public static async Task<CdpClient> ConnectAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var ready = false;
        while (!ready && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ready = !string.IsNullOrWhiteSpace(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Браузер ещё поднимается.
            }

            if (!ready)
                await Task.Delay(150, ct);
        }

        if (!ready)
            throw new InvalidOperationException("отладочный порт браузера не поднялся.");

        // TARGET: свой tab, а не случайная служебная edge:// страница (sync-confirmation-dialog).
        var webSocketUrl = await TryCreateTargetAsync(http, port, ct) ?? await FindPageTargetAsync(http, port, ct);
        if (webSocketUrl is null)
            throw new InvalidOperationException("не удалось подключиться к отладочному порту браузера.");

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(new Uri(webSocketUrl), ct);
        return new CdpClient(socket);
    }

    private static async Task<string?> TryCreateTargetAsync(HttpClient http, int port, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?about:blank"), ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws) ? ws.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> FindPageTargetAsync(HttpClient http, int port, CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
            using var doc = JsonDocument.Parse(json);
            string? fallback = null;
            foreach (var target in doc.RootElement.EnumerateArray())
            {
                if (!target.TryGetProperty("type", out var type) || type.GetString() != "page")
                    continue;
                if (!target.TryGetProperty("webSocketDebuggerUrl", out var ws) || ws.GetString() is not { Length: > 0 } url)
                    continue;
                var targetUrl = target.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                if (targetUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || targetUrl.StartsWith("file", StringComparison.OrdinalIgnoreCase)
                    || targetUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    return url;
                fallback ??= url;
            }

            return fallback;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Последовательный запрос: отправляем команду и читаем ответ (события попутно собираем).</summary>
    public async Task<JsonElement?> SendAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var paramsJson = parameters is null ? "{}" : JsonSerializer.Serialize(parameters);
        var payloadJson = "{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}";

        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var builder = new StringBuilder();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            string message;
            try
            {
                message = await ReceiveMessageAsync(buffer, builder, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"CDP {method}: браузер не ответил за 15 с");
            }

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var responseId) && responseId == id)
            {
                if (root.TryGetProperty("error", out _))
                    throw new InvalidOperationException("CDP error: " + message);
                return root.TryGetProperty("result", out var result) ? result.Clone() : null;
            }

            TrackEvent(root);
        }
    }

    private async Task<string> ReceiveMessageAsync(byte[] buffer, StringBuilder builder, CancellationToken ct)
    {
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("браузер закрыл соединение: " + (result.CloseStatusDescription ?? "no reason"));
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
                continue;
            var message = builder.ToString();
            builder.Clear();
            return message;
        }
    }

    private void TrackEvent(JsonElement root)
    {
        if (root.TryGetProperty("method", out var method) && method.GetString() == "Runtime.exceptionThrown"
            && root.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("exceptionDetails", out var details)
            && details.TryGetProperty("text", out var text)
            && text.GetString() is { Length: > 0 } description)
        {
            lock (_runtimeErrors)
            {
                if (_runtimeErrors.Count < 20 && !_runtimeErrors.Contains(description))
                    _runtimeErrors.Add(description);
            }
        }
    }

    public async Task<JsonElement?> EvaluateAsync(string expression, CancellationToken ct)
    {
        var result = await SendAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
        }, ct);
        if (result is not { } root)
            return null;
        if (root.TryGetProperty("exceptionDetails", out var exception))
        {
            var message = exception.TryGetProperty("text", out var text) ? text.GetString() : "JS exception";
            throw new InvalidOperationException("ошибка JS на странице: " + message);
        }

        if (root.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var resultValue))
            return resultValue.Clone();
        return null;
    }

    public async Task<StepOutcome> PressKeyAsync(string key, CancellationToken ct)
    {
        var (code, virtualKey, text) = key.ToUpperInvariant() switch
        {
            "ENTER" => ("Enter", 13, "\r"),
            "SPACE" => (" ", 32, " "),
            "TAB" => ("Tab", 9, "\t"),
            "ESCAPE" or "ESC" => ("Escape", 27, null),
            _ => (key, 0, null),
        };
        if (virtualKey == 0)
            return new StepOutcome(false, $"неизвестная клавиша «{key}»");
        await SendAsync("Input.dispatchKeyEvent", new
        {
            type = "keyDown",
            windowsVirtualKeyCode = virtualKey,
            code,
            key,
            text,
        }, ct);
        await SendAsync("Input.dispatchKeyEvent", new
        {
            type = "keyUp",
            windowsVirtualKeyCode = virtualKey,
            code,
            key,
        }, ct);
        return new StepOutcome(true, null);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or TimeoutException or InvalidOperationException)
        {
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }
}