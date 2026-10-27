using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop.Mcp;

/// <summary>Сохранённое значение поля сервиса: секреты шифруются DPAPI, остальное (поддомен, ID) лежит как есть.</summary>
public sealed class StoredValue
{
    public bool Secret { get; set; }
    public string Value { get; set; } = string.Empty;
}

/// <summary>Подключённые REST-сервисы на этом ПК: %APPDATA%\ConexyAI\services.json.</summary>
public sealed class ServicesState
{
    public Dictionary<string, Dictionary<string, StoredValue>> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI", "services.json");

    public static ServicesState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<ServicesState>(File.ReadAllText(path)) ?? new ServicesState();
                state.Services = new(state.Services, StringComparer.OrdinalIgnoreCase);
                return state;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new ServicesState();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>Готовый к отправке запрос — отдельно от отправки, чтобы раскладку параметров можно было проверить без сети.</summary>
public sealed record BuiltRequest(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body, bool Write);

/// <summary>
/// Встроенный коннектор REST-сервисов РФ (Яндекс Диск, Telegram-бот, ВКонтакте, Битрикс24, Трекер, Метрика, DaData,
/// МойСклад, amoCRM, ЮKassa, ЦБ РФ). Агенту отдаются ровно два инструмента — «список» и «вызов», — поэтому лимит
/// инструментов (48) не страдает, сколько бы сервисов ни было в каталоге. Запись всегда подтверждает человек.
/// </summary>
public sealed class ServiceConnector
{
    public const string ServerSlug = "svc";
    private const int MaxOutputChars = 60_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    private readonly ICommandApprover _approver;
    private readonly HttpClient _http;
    private readonly string _statePath;
    private ServicesState _state;

    public ServiceConnector(ICommandApprover approver, HttpMessageHandler? handler = null, string? statePath = null)
    {
        _approver = approver;
        _statePath = statePath ?? ServicesState.DefaultPath;
        _state = ServicesState.Load(_statePath);
        // Без автоперехода: ключ в адресе или заголовке не должен уйти на чужой хост по редиректу.
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = Timeout };
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Ids => string.Join(", ", ServiceCatalog.Services.Select(s => s.Id));

    public IReadOnlyList<LocalToolSpec> Tools { get; } =
    [
        new("local__svc__list",
            "Сервисы РФ (Яндекс Диск, Telegram-бот, MAX, ВКонтакте, Битрикс24, Трекер, Метрика, Яндекс 360, DaData, МойСклад, amoCRM, ЮKassa, Ozon, Яндекс Маркет, Контур.Фокус, SMS.ru, UniSender, 2ГИС, Геокодер, курсы ЦБ): какие подключены и что умеют. " +
            "С service — полное описание операций и параметров; с query — поиск по сервисам и операциям.",
            Schema("""{"type":"object","properties":{"service":{"type":"string","description":"id сервиса — вернёт его операции и параметры"},"query":{"type":"string","description":"Слово для поиска по сервисам и операциям"}}}""")),
        new("local__svc__call",
            "Вызвать операцию сервиса РФ. Сначала local__svc__list, чтобы узнать операции. Запись (отправить, создать, удалить) подтверждает человек. Сервисы: " + Ids + ".",
            Schema("""{"type":"object","properties":{"service":{"type":"string","description":"id сервиса"},"operation":{"type":"string","description":"имя операции"},"params":{"type":"object","description":"параметры операции по описанию из list"},"data":{"description":"тело или свободные аргументы метода (для универсальных операций api и операций создания)"}},"required":["service","operation"]}"""))
    ];

    // ── Подключение ─────────────────────────────────────────────────────────

    public bool IsConnected(ServiceDef def) =>
        (def.Inputs ?? []).Where(i => !i.Optional).All(i => _state.Services.TryGetValue(def.Id, out var v) && v.TryGetValue(i.Name, out var s) && s.Value.Length > 0);

    public IEnumerable<ServiceDef> Connected => ServiceCatalog.Services.Where(IsConnected);

    /// <summary>Нужен ли вообще вход: у «ЦБ РФ» ключей нет, он доступен сразу.</summary>
    public static bool NeedsLogin(ServiceDef def) => (def.Inputs ?? []).Any(i => !i.Optional);

    public void SaveCredentials(ServiceDef def, IReadOnlyDictionary<string, string> values)
    {
        var stored = new Dictionary<string, StoredValue>(StringComparer.Ordinal);
        foreach (var input in def.Inputs ?? [])
        {
            if (!values.TryGetValue(input.Name, out var value) || value.Length == 0)
                continue;
            stored[input.Name] = new StoredValue { Secret = input.Secret, Value = input.Secret ? SecretBox.Protect(value) : value };
        }
        _state.Services[def.Id] = stored;
        _state.Save(_statePath);
    }

    public bool Remove(string id)
    {
        var removed = _state.Services.Remove(id);
        if (removed)
            _state.Save(_statePath);
        return removed;
    }

    /// <summary>Проверочный вызов после ввода ключа: сразу видно, принял ли сервис токен.</summary>
    public async Task<(bool Ok, string Message)> CheckAsync(ServiceDef def, CancellationToken ct)
    {
        if (def.CheckOp is null)
            return (true, "сохранено");
        var op = def.Ops.First(o => o.Name == def.CheckOp);
        // Универсальные операции проверяются безобидным методом чтения.
        var args = op.Path.Contains("{method}", StringComparison.Ordinal)
            ? new Dictionary<string, string> { ["method"] = def.Id switch { "telegram_bot" => "getMe", "vk" => "users.get", "bitrix24" => "user.current", "unisender" => "getLists", _ => "get" } }
            : op.Params?.Where(p => p.Required).ToDictionary(p => p.Name, p => p.Name == "query" ? "7707083893" : "1")
                ?? new Dictionary<string, string>();
        var result = await InvokeAsync(def, op, args, null, ct, skipApproval: true);
        return (!result.IsError, result.IsError ? result.Output : "ключ принят");
    }

    // ── Инструменты агента ──────────────────────────────────────────────────

    public async Task<ToolResult> CallAsync(string tool, ToolArgs args, CancellationToken ct)
    {
        try
        {
            return tool switch
            {
                "list" => ToolResult.Ok(Describe(args.Str("service"), args.Str("query"))),
                "call" => await CallServiceAsync(args, ct),
                _ => ToolResult.Fail($"Неизвестный инструмент: {tool}"),
            };
        }
        catch (ToolException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private async Task<ToolResult> CallServiceAsync(ToolArgs args, CancellationToken ct)
    {
        var id = args.Required("service", "svc call");
        var def = ServiceCatalog.Find(id);
        if (def is null)
            return ToolResult.Fail($"Нет сервиса «{id}». Доступны: {Ids}.");
        if (!IsConnected(def))
            return ToolResult.Fail($"«{def.Title}» не подключён. Попросите пользователя выполнить в терминале: /connect {def.Id}");

        var opName = args.Required("operation", "svc call");
        var op = def.Ops.FirstOrDefault(o => o.Name.Equals(opName, StringComparison.OrdinalIgnoreCase));
        if (op is null)
            return ToolResult.Fail($"У «{def.Id}» нет операции «{opName}». Есть: {string.Join(", ", def.Ops.Select(o => o.Name))}.");

        return await InvokeAsync(def, op, args.Dict("params"), args.Raw("data"), ct, skipApproval: false);
    }

    private async Task<ToolResult> InvokeAsync(
        ServiceDef def, SvcOp op, IReadOnlyDictionary<string, string> parameters, JsonElement? data, CancellationToken ct, bool skipApproval)
    {
        var values = Values(def);
        BuiltRequest request;
        try
        {
            request = Build(def, op, parameters, data, values);
        }
        catch (ToolException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        if (request.Write && !skipApproval)
        {
            var preview = $"{def.Title} · {op.Name}" + (parameters.Count > 0 ? "\n" + Truncate(JsonSerializer.Serialize(parameters, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), 400) : string.Empty)
                          + (request.Body is { Length: > 0 } ? "\n" + Truncate(request.Body, 600) : string.Empty);
            if (!await _approver.ApproveSensitiveAsync($"{def.Title}: {op.Description.Split('.')[0]}", Scrub(preview, values, def), "сервис " + def.Id, ct))
                return ToolResult.Fail("USER_REJECTED: пользователь не разрешил это действие. Не повторяй его; спроси, что делать.");
        }

        using var message = new HttpRequestMessage(request.Method, request.Url);
        foreach (var (k, v) in request.Headers)
            message.Headers.TryAddWithoutValidation(k, v);
        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            text = Scrub(text, values, def);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var target = response.Headers.Location is { } l ? (l.IsAbsoluteUri ? l.Host : l.ToString()) : "?";
                return ToolResult.Fail($"{def.Title}: сервис ответил перенаправлением (HTTP {(int)response.StatusCode}) на {target} — по ним клиент не ходит. Проверьте адрес и ключ (/connect {def.Id}).");
            }
            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"{def.Title}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}" + (text.Length > 0 ? "\n" + Truncate(text, 2000) : string.Empty)
                    + (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? $"\nКлюч не принят или не хватает прав — пользователь может переподключить: /connect {def.Id}" : string.Empty));

            if (text.Length == 0)
                return ToolResult.Ok($"(пустой ответ, HTTP {(int)response.StatusCode})");
            return ToolResult.Ok(text.Length > MaxOutputChars ? text[..MaxOutputChars] + $"\n[… ответ обрезан: всего {text.Length} символов]" : text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Fail($"{def.Title}: сервис не ответил за {(int)Timeout.TotalSeconds} с.");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail($"{def.Title}: {Scrub(ex.Message, values, def)}");
        }
    }

    // ── Раскладка запроса ───────────────────────────────────────────────────

    /// <summary>Расшифрованные значения полей сервиса для подстановки.</summary>
    private Dictionary<string, string> Values(ServiceDef def)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_state.Services.TryGetValue(def.Id, out var stored))
            return result;
        foreach (var (name, v) in stored)
            result[name] = v.Secret ? SecretBox.Unprotect(v.Value) ?? string.Empty : v.Value;
        return result;
    }

    public static BuiltRequest Build(
        ServiceDef def, SvcOp op, IReadOnlyDictionary<string, string> parameters, JsonElement? data, IReadOnlyDictionary<string, string> values)
    {
        var declared = op.Params ?? [];
        foreach (var name in parameters.Keys)
            if (!declared.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"У операции «{op.Name}» нет параметра «{name}». Есть: {(declared.Length == 0 ? "(без параметров)" : string.Join(", ", declared.Select(p => p.Name)))}.");

        string? Get(SvcParam p) => parameters.FirstOrDefault(kv => kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Value;

        foreach (var p in declared.Where(p => p.Required))
            if (string.IsNullOrWhiteSpace(Get(p)))
                throw new ToolException($"Нужен параметр «{p.Name}» ({p.Description}).");

        // Путь: подставляем параметры по сегментам — «/» и «..» из значения не должны менять адрес.
        var path = op.Path;
        string? method = null;
        foreach (var p in declared.Where(p => p.In == SvcIn.Path))
        {
            var value = Get(p)!.Trim();
            var segments = value.Split('/');
            if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.Contains('\\')))
                throw new ToolException($"Недопустимое значение «{p.Name}»: «{value}».");
            if (p.Name == "method")
            {
                if (!Regex.IsMatch(value, @"^[A-Za-z0-9._]{1,80}$"))
                    throw new ToolException($"Недопустимое имя метода «{value}».");
                method = value;
            }
            path = path.Replace("{" + p.Name + "}", string.Join('/', segments.Select(Uri.EscapeDataString)), StringComparison.Ordinal);
        }

        // Запись: операция помечена так сама либо универсальный метод не из списка чтения.
        var write = op.Write || (method is not null && !IsReadMethod(method, op.ReadVerbs));

        var query = new List<(string, string)>();
        foreach (var (k, v) in def.Query ?? [])
            query.Add((k, Expand(v, values)));
        foreach (var p in declared.Where(p => p.In == SvcIn.Query))
            if (Get(p) is { Length: > 0 } v)
                query.Add((p.Name, v));

        JsonNode? body = null;
        var bodyParams = declared.Where(p => p.In == SvcIn.Body && Get(p) is { Length: > 0 }).ToList();
        if (bodyParams.Count > 0)
        {
            var obj = new JsonObject();
            foreach (var p in bodyParams)
                obj[p.Name] = TypedNode(p, Get(p)!);
            body = obj;
        }

        if (data is not null && op.Data == SvcData.None)
            throw new ToolException($"Операция «{op.Name}» не принимает data — аргументы идут в params.");

        if (data is { } extra)
        {
            if (op.Data == SvcData.Query)
            {
                if (extra.ValueKind != JsonValueKind.Object)
                    throw new ToolException("data должен быть JSON-объектом с параметрами метода.");
                foreach (var prop in extra.EnumerateObject())
                    query.Add((prop.Name, QueryValue(prop.Value)));
            }
            else if (extra.ValueKind == JsonValueKind.Object)
            {
                body ??= new JsonObject();
                if (body is not JsonObject target)
                    throw new ToolException("data нельзя совместить с параметрами тела этой операции.");
                foreach (var prop in extra.EnumerateObject())
                    if (!target.ContainsKey(prop.Name))
                        target[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
            else if (extra.ValueKind == JsonValueKind.Array)
            {
                if (body is not null)
                    throw new ToolException("Массив в data нельзя совместить с параметрами тела.");
                body = JsonNode.Parse(extra.GetRawText());
            }
            else
            {
                throw new ToolException("data должен быть JSON-объектом или массивом.");
            }
        }

        // Адрес: база (с подстановкой ввода пользователя) + путь + query.
        var baseUrl = Expand(def.BaseUrl, values).TrimEnd('/');
        var full = baseUrl + (path.StartsWith('/') ? path : "/" + path);
        if (query.Count > 0)
            full += "?" + string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Item1) + "=" + Uri.EscapeDataString(kv.Item2)));
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            throw new ToolException($"Адрес сервиса «{def.Title}» некорректен — переподключите: /connect {def.Id}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in def.Headers ?? [])
        {
            var value = Expand(v, values);
            if (value.Trim().Length > 0 && !value.EndsWith(' '))
                headers[k] = value;
        }
        foreach (var p in declared.Where(p => p.In == SvcIn.Header && Get(p) is { Length: > 0 }))
            headers[p.Name] = Get(p)!;

        var httpMethod = new HttpMethod(op.Method.ToUpperInvariant());
        var needsBody = body is not null || (op.Data == SvcData.Json && httpMethod != HttpMethod.Get && httpMethod != HttpMethod.Delete);
        return new BuiltRequest(httpMethod, uri, headers, needsBody ? (body ?? new JsonObject()).ToJsonString() : null, write);
    }

    private static JsonNode? TypedNode(SvcParam p, string value) => p.Type switch
    {
        "integer" when long.TryParse(value, out var l) => JsonValue.Create(l),
        "number" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        "boolean" when bool.TryParse(value, out var b) => JsonValue.Create(b),
        _ => JsonValue.Create(value),
    };

    private static string QueryValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(',', v.EnumerateArray().Select(QueryValue)),
        _ => v.GetRawText(),
    };

    /// <summary>
    /// Метод считается чтением, если его последнее слово совпадает с названием из списка; «get*» — любое слово на «get…».
    /// Всё неизвестное — запись: лучше лишний раз спросить человека, чем изменить данные молча.
    /// </summary>
    public static bool IsReadMethod(string method, string[]? readVerbs)
    {
        if (readVerbs is null)
            return false;
        var verb = method[(method.LastIndexOf('.') + 1)..];
        return readVerbs.Any(r => r.EndsWith('*')
            ? verb.StartsWith(r[..^1], StringComparison.OrdinalIgnoreCase)
            : verb.Equals(r, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex B64 = new(@"\$\{b64:((?:[^{}]|\$\{[A-Za-z0-9_]+\})*)\}", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    /// <summary>Подставляет ${ИМЯ} значениями; ${b64:…} — base64 от уже подставленного (для Basic-авторизации).</summary>
    public static string Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        string Sub(string s) => Placeholder.Replace(s, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty);
        var withB64 = B64.Replace(template, m => Convert.ToBase64String(Encoding.UTF8.GetBytes(Sub(m.Groups[1].Value))));
        return Sub(withB64);
    }

    /// <summary>Убирает ключи из любого текста, который уйдёт агенту или в окно подтверждения.</summary>
    private static string Scrub(string text, IReadOnlyDictionary<string, string> values, ServiceDef def)
    {
        foreach (var input in def.Inputs ?? [])
        {
            if (!input.Secret || !values.TryGetValue(input.Name, out var secret) || secret.Length < 6)
                continue;
            text = text.Replace(secret, "***", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret), "***", StringComparison.Ordinal);
        }
        return text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ── Описание для агента ─────────────────────────────────────────────────

    public string Describe(string? service, string? query)
    {
        if (!string.IsNullOrWhiteSpace(service))
        {
            var def = ServiceCatalog.Find(service.Trim());
            return def is null
                ? $"Нет сервиса «{service}». Доступны: {Ids}."
                : DescribeService(def);
        }

        var q = query?.Trim();
        var sb = new StringBuilder();
        var matches = ServiceCatalog.Services.Where(s => q is not { Length: > 0 } || Matches(s, q)).ToList();
        var connected = matches.Where(IsConnected).ToList();
        var other = matches.Where(s => !IsConnected(s)).ToList();

        sb.AppendLine(connected.Count > 0 ? "Подключено:" : "Подключённых сервисов нет.");
        foreach (var s in connected)
            sb.AppendLine($"- {s.Id} — {s.Title}: {s.Description} Операции: {string.Join(", ", s.Ops.Select(o => o.Name))}.");
        if (other.Count > 0)
        {
            sb.AppendLine("Не подключены (подключает пользователь командой /connect <id>):");
            foreach (var s in other)
                sb.AppendLine($"- {s.Id} — {s.Title}");
        }
        sb.AppendLine("Подробности по сервису: local__svc__list с service=<id>.");
        return sb.ToString().TrimEnd();
    }

    private static bool Matches(ServiceDef s, string q) =>
        s.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.Ops.Any(o => o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || o.Description.Contains(q, StringComparison.OrdinalIgnoreCase));

    private string DescribeService(ServiceDef def)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{def.Id} — {def.Title}. {def.Description}");
        if (!IsConnected(def))
            sb.AppendLine($"НЕ ПОДКЛЮЧЁН: попросите пользователя выполнить /connect {def.Id}");
        if (def.Note is { } note)
            sb.AppendLine("Примечание: " + note);
        foreach (var op in def.Ops)
        {
            var write = op.Write || op.Path.Contains("{method}", StringComparison.Ordinal);
            sb.AppendLine($"\n• {op.Name} — {op.Description}" + (op.Write ? " [запись, подтвердит пользователь]" : write ? " [чтение без подтверждения; запись — с подтверждением]" : string.Empty));
            foreach (var p in op.Params ?? [])
                sb.AppendLine($"    params.{p.Name}{(p.Required ? " *" : string.Empty)} ({p.Type}) — {p.Description}");
            if (op.Data != SvcData.None)
                sb.AppendLine("    data — " + (op.DataHelp ?? "дополнительные поля запроса как JSON."));
        }
        return sb.ToString().TrimEnd();
    }
}
