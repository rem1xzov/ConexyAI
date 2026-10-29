using System.Text;
using System.Text.Json;

namespace ConexyAI.Service.Phone;

// PHONE_AGENT: добавлено 2026-10-29 — каталог инструментов phone_* (см. frontend/PHONE_AGENT.md).
// Схемы для модели, класс опасности (R/C/D), подписи статуса и проверка аргументов живут в одном месте:
// сервер никогда не отдаёт телефону аргументы, которых нет в схеме.

/// <summary>R — только чтение; C — сервер спрашивает подтверждение; D — опасное (красная карточка).</summary>
public enum PhoneToolClass
{
    Read,
    Confirm,
    Dangerous,
}

public sealed record PhoneParam(string Name, string Type, string Description, bool Required = false, string[]? Enum = null);

public sealed record PhoneToolSpec(
    string Name,
    string Description,
    PhoneToolClass Class,
    IReadOnlyList<PhoneParam> Params,
    string Label)
{
    public bool NeedsApproval => Class != PhoneToolClass.Read;
}

public static class PhoneToolCatalog
{
    public const string Prefix = "phone_";

    public static readonly string[] Permissions =
    {
        "camera", "microphone", "photos", "videos", "audio", "notifications", "contacts", "location",
        "usage_access", "all_files", "accessibility", "battery_optimization", "install_packages", "overlay", "write_settings",
    };

    public static readonly string[] SettingsScreens =
    {
        "app_details", "battery", "battery_optimization", "storage", "usage_access", "accessibility", "notifications",
        "app_notifications", "wifi", "bluetooth", "display", "sound", "location", "developer", "all_files",
        "default_apps", "date", "home", "apps",
    };

    private const int MaxText = 4_000;
    private const int MaxContent = 200_000;

    private static PhoneParam S(string name, string desc, bool required = false, string[]? values = null) =>
        new(name, "string", desc, required, values);
    private static PhoneParam I(string name, string desc) => new(name, "integer", desc);
    private static PhoneParam B(string name, string desc) => new(name, "boolean", desc);

    private static readonly PhoneToolSpec[] Specs =
    {
        new("phone_device_info", "Модель, производитель, версия Android и приложения. Вызывай в начале, если нужно знать, на каком устройстве работаешь.",
            PhoneToolClass.Read, Array.Empty<PhoneParam>(), "Смотрю сведения о телефоне…"),
        new("phone_permissions", "Какие разрешения уже выданы агенту на телефоне (granted/denied/prompt/unavailable). ВСЕГДА вызывай первым, прежде чем использовать галерею, файлы, экран, статистику приложений.",
            PhoneToolClass.Read, Array.Empty<PhoneParam>(), "Проверяю разрешения на телефоне…"),
        new("phone_request_permission", "Просит пользователя выдать разрешение: на телефоне появится системный диалог или откроется нужный экран настроек, инструмент ждёт возврата. Перед вызовом объясни пользователю, зачем оно нужно.",
            PhoneToolClass.Read, new[] { S("permission", "Какое разрешение запросить", true, Permissions) }, "Запрашиваю разрешение на телефоне…"),
        new("phone_apps_list", "Список установленных приложений: имя, пакет, размер, время последнего использования, время в фоне. Для диагностики («телефон тормозит», «что занимает место», «что не использую»).",
            PhoneToolClass.Read, new[]
            {
                S("filter", "Какие приложения (по умолчанию user)", false, new[] { "all", "user", "system" }),
                S("sort", "Сортировка", false, new[] { "last_used", "size", "name", "foreground_time" }),
                I("limit", "Сколько записей вернуть (по умолчанию 50)"),
            }, "Смотрю приложения на телефоне…"),
        new("phone_battery_report", "Отчёт о батарее: заряд, состояние, температура, расход по приложениям. Нужен для «быстро садится батарея».",
            PhoneToolClass.Read, Array.Empty<PhoneParam>(), "Смотрю батарею телефона…"),
        new("phone_storage_report", "Отчёт о памяти: сколько занято и свободно, что занимает место (приложения, кэш, медиа).",
            PhoneToolClass.Read, Array.Empty<PhoneParam>(), "Смотрю память телефона…"),
        new("phone_open_settings", "Открывает экран системных настроек на телефоне (пользователь сам всё поменяет). Для действий, которые нельзя сделать напрямую.",
            PhoneToolClass.Read, new[]
            {
                S("screen", "Какой экран настроек открыть", true, SettingsScreens),
                S("package", "Пакет приложения (для app_details и app_notifications)"),
            }, "Открываю настройки на телефоне…"),
        new("phone_launch_app", "Запускает приложение по имени пакета (пакет бери из phone_apps_list).",
            PhoneToolClass.Read, new[] { S("package", "Имя пакета, например com.android.chrome", true) }, "Запускаю приложение на телефоне…"),
        new("phone_open_url", "Открывает ссылку в браузере телефона.",
            PhoneToolClass.Read, new[] { S("url", "http(s) адрес", true) }, "Открываю ссылку на телефоне…"),
        new("phone_uninstall_app", "Удаляет приложение. ОПАСНО: сначала пользователь подтверждает в чате и в уведомлении. Предлагай только неиспользуемые приложения и объясняй почему.",
            PhoneToolClass.Dangerous, new[] { S("package", "Имя пакета приложения", true) }, "Удаляю приложение с телефона…"),
        new("phone_clear_cache", "Очищает кэш одного приложения или (без аргумента) всех приложений. Требует подтверждения пользователя. Личные данные и настройки приложений не затрагивает.",
            PhoneToolClass.Confirm, new[] { S("package", "Имя пакета; без него — кэш всех приложений") }, "Очищаю кэш на телефоне…"),
        new("phone_force_stop_app", "Принудительно останавливает приложение (например, зависшее или тратящее батарею). Требует подтверждения.",
            PhoneToolClass.Confirm, new[] { S("package", "Имя пакета приложения", true) }, "Останавливаю приложение на телефоне…"),
        new("phone_screen_read", "Читает, что сейчас на экране телефона (дерево элементов: тексты, кнопки, node_id). Нужна служба специальных возможностей. Вызывай перед тапами, чтобы выбрать цель.",
            PhoneToolClass.Read, Array.Empty<PhoneParam>(), "Читаю экран телефона…"),
        new("phone_screenshot", "Скриншот экрана телефона — картинка приходит тебе для визуальной проверки. Нужна служба специальных возможностей.",
            PhoneToolClass.Read, Array.Empty<PhoneParam>(), "Делаю снимок экрана телефона…"),
        new("phone_tap", "Нажимает на экране: по тексту элемента, по node_id из phone_screen_read или по координатам x/y. long_press — долгое нажатие.",
            PhoneToolClass.Read, new[]
            {
                S("text", "Текст элемента"), S("node_id", "Идентификатор элемента из phone_screen_read"),
                I("x", "Координата X в пикселях"), I("y", "Координата Y в пикселях"), B("long_press", "Долгое нажатие"),
            }, "Нажимаю на экране телефона…"),
        new("phone_swipe", "Свайп по экрану: направление (up/down/left/right) или точные координаты x1,y1 → x2,y2.",
            PhoneToolClass.Read, new[]
            {
                S("direction", "Направление свайпа", false, new[] { "up", "down", "left", "right" }),
                I("x1", "Начало X"), I("y1", "Начало Y"), I("x2", "Конец X"), I("y2", "Конец Y"),
                I("duration_ms", "Длительность жеста, мс"),
            }, "Листаю экран телефона…"),
        new("phone_type_text", "Вводит текст в поле (по node_id или в поле с фокусом).",
            PhoneToolClass.Read, new[] { S("text", "Текст для ввода", true), S("node_id", "Идентификатор поля из phone_screen_read") }, "Ввожу текст на телефоне…"),
        new("phone_global_action", "Системное действие: назад, домой, недавние, шторка уведомлений, быстрые настройки, блокировка экрана.",
            PhoneToolClass.Read, new[] { S("action", "Действие", true, new[] { "back", "home", "recents", "notifications", "quick_settings", "lock_screen" }) },
            "Выполняю системное действие на телефоне…"),
        new("phone_gallery_list", "Список последних фото/видео из галереи (uri, дата, размер). Нужны разрешения photos/videos.",
            PhoneToolClass.Read, new[]
            {
                S("media", "Тип медиа", false, new[] { "images", "videos" }),
                I("limit", "Сколько записей (по умолчанию 30)"),
            }, "Смотрю галерею телефона…"),
        new("phone_gallery_pick", "Открывает на телефоне выбор фото/видео — пользователь выбирает сам (до 10 минут). Возвращает uri и превью. Используй, когда нужно «вот эту фотку», а не угадывать.",
            PhoneToolClass.Read, new[]
            {
                S("media", "Тип медиа", false, new[] { "images", "videos" }),
                B("multiple", "Разрешить выбрать несколько"),
            }, "Жду, пока выберете фото на телефоне…"),
        new("phone_image_view", "Показывает тебе картинку из галереи/файла по uri — ты её видишь как изображение.",
            PhoneToolClass.Read, new[] { S("uri", "uri из phone_gallery_list/phone_gallery_pick", true) }, "Смотрю фото на телефоне…"),
        new("phone_widget_photo", "Ставит выбранное фото виджетом на главный экран (с подписью, если задана). Типичный сценарий: phone_gallery_pick → phone_widget_photo.",
            PhoneToolClass.Read, new[] { S("uri", "uri фото", true), S("caption", "Подпись под фото") }, "Делаю виджет с фото на телефоне…"),
        new("phone_set_wallpaper", "Ставит фото обоями. Меняет внешний вид телефона — требует подтверждения.",
            PhoneToolClass.Confirm, new[]
            {
                S("uri", "uri фото", true),
                S("target", "Куда поставить", false, new[] { "home", "lock", "both" }),
            }, "Ставлю обои на телефоне…"),
        new("phone_file_list", "Содержимое папки на телефоне. Относительные пути — от рабочей папки агента (Documents/ConexyAI).",
            PhoneToolClass.Read, new[] { S("path", "Путь к папке"), B("show_hidden", "Показывать скрытые") }, "Смотрю файлы на телефоне…"),
        new("phone_file_read", "Читает текстовый файл на телефоне (с offset/limit для больших).",
            PhoneToolClass.Read, new[] { S("path", "Путь к файлу", true), I("offset", "С какой строки"), I("limit", "Сколько строк") }, "Читаю файл на телефоне…"),
        new("phone_file_search", "Ищет файлы на телефоне по имени (pattern, например *.pdf) и/или содержимому (contains).",
            PhoneToolClass.Read, new[]
            {
                S("path", "Где искать"), S("pattern", "Маска имени"), S("contains", "Текст внутри файла"), I("limit", "Сколько результатов"),
            }, "Ищу файлы на телефоне…"),
        new("phone_file_write", "Записывает файл на телефоне (append — дописать). Требует подтверждения.",
            PhoneToolClass.Confirm, new[] { S("path", "Путь к файлу", true), S("content", "Содержимое", true), B("append", "Дописать в конец") }, "Пишу файл на телефоне…"),
        new("phone_file_mkdir", "Создаёт папку на телефоне. Требует подтверждения.",
            PhoneToolClass.Confirm, new[] { S("path", "Путь к папке", true) }, "Создаю папку на телефоне…"),
        new("phone_file_copy", "Копирует файл/папку на телефоне. Требует подтверждения.",
            PhoneToolClass.Confirm, new[] { S("from", "Откуда", true), S("to", "Куда", true) }, "Копирую файл на телефоне…"),
        new("phone_file_move", "Перемещает/переименовывает файл или папку на телефоне. Требует подтверждения.",
            PhoneToolClass.Confirm, new[] { S("from", "Откуда", true), S("to", "Куда", true) }, "Перемещаю файл на телефоне…"),
        new("phone_file_delete", "Удаляет файл или папку на телефоне. ОПАСНО: подтверждает пользователь. Не удаляй без явной просьбы или согласия.",
            PhoneToolClass.Dangerous, new[] { S("path", "Путь", true), B("recursive", "Удалить папку со всем содержимым") }, "Удаляю файл на телефоне…"),
        new("phone_share", "Открывает системное меню «Поделиться» с файлом и/или текстом — отправляет пользователь сам.",
            PhoneToolClass.Read, new[] { S("path", "Файл для отправки"), S("text", "Текст для отправки") }, "Открываю «Поделиться» на телефоне…"),
        new("phone_clipboard_set", "Кладёт текст в буфер обмена телефона.",
            PhoneToolClass.Read, new[] { S("text", "Текст", true) }, "Копирую в буфер обмена телефона…"),
    };

    private static readonly Dictionary<string, PhoneToolSpec> ByName =
        Specs.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public static IReadOnlyList<PhoneToolSpec> All => Specs;

    public static bool IsPhoneTool(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) && ByName.ContainsKey(name);

    public static PhoneToolSpec? Get(string? name) =>
        name is not null && ByName.TryGetValue(name, out var spec) ? spec : null;

    public static PhoneToolClass Classify(string name) => Get(name)?.Class ?? PhoneToolClass.Dangerous;

    public static string Label(string tool) => Get(tool)?.Label ?? "Работаю с телефоном…";

    /// <summary>Сколько сервер ждёт ответ телефона. Системные диалоги и выбор фото — долго.</summary>
    public static TimeSpan TimeoutFor(string tool) => tool is "phone_gallery_pick" or "phone_request_permission"
        ? TimeSpan.FromMinutes(10)
        : TimeSpan.FromMinutes(5);

    /// <summary>JSON-схемы в формате function-calling (как остальные инструменты агента).</summary>
    public static IReadOnlyList<object> Schemas() => Specs.Select(Schema).ToList();

    public static object Schema(PhoneToolSpec spec)
    {
        var properties = new Dictionary<string, object>();
        foreach (var p in spec.Params)
        {
            var prop = new Dictionary<string, object> { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum is not null)
                prop["enum"] = p.Enum;
            properties[p.Name] = prop;
        }

        var parameters = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties };
        var required = spec.Params.Where(p => p.Required).Select(p => p.Name).ToArray();
        if (required.Length > 0)
            parameters["required"] = required;

        return new
        {
            type = "function",
            function = new { name = spec.Name, description = spec.Description, parameters },
        };
    }

    /// <summary>
    /// Проверяет аргументы по схеме инструмента и возвращает нормализованный JSON (только известные поля).
    /// Ошибка — понятный текст для модели.
    /// </summary>
    public static bool TryNormalizeArguments(string tool, string? argumentsJson, out string normalized, out string? error)
    {
        normalized = "{}";
        error = null;
        var spec = Get(tool);
        if (spec is null)
        {
            error = $"Неизвестный инструмент телефона '{tool}'.";
            return false;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = $"{tool}: аргументы — не корректный JSON.";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = $"{tool}: аргументы должны быть JSON-объектом.";
            return false;
        }

        var result = new Dictionary<string, object>();
        foreach (var p in spec.Params)
        {
            if (!root.TryGetProperty(p.Name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (p.Required)
                {
                    error = $"{tool}: нужен аргумент '{p.Name}'.";
                    return false;
                }
                continue;
            }

            switch (p.Type)
            {
                case "string":
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        error = $"{tool}: '{p.Name}' должен быть строкой.";
                        return false;
                    }
                    var text = (value.GetString() ?? string.Empty).Replace("\0", string.Empty);
                    var max = p.Name == "content" ? MaxContent : MaxText;
                    if (text.Length > max)
                    {
                        error = $"{tool}: '{p.Name}' слишком длинный (макс. {max} символов).";
                        return false;
                    }
                    if (p.Required && text.Trim().Length == 0)
                    {
                        if (p.Name != "content")
                        {
                            error = $"{tool}: '{p.Name}' не должен быть пустым.";
                            return false;
                        }
                    }
                    if (p.Enum is not null && !p.Enum.Contains(text, StringComparer.Ordinal))
                    {
                        error = $"{tool}: '{p.Name}' должен быть одним из: {string.Join(", ", p.Enum)}.";
                        return false;
                    }
                    result[p.Name] = text;
                    break;
                }
                case "integer":
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var d) || double.IsNaN(d) || Math.Abs(d) > 1e9)
                    {
                        error = $"{tool}: '{p.Name}' должен быть числом.";
                        return false;
                    }
                    result[p.Name] = (long)Math.Round(d);
                    break;
                }
                case "boolean":
                {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        error = $"{tool}: '{p.Name}' должен быть true или false.";
                        return false;
                    }
                    result[p.Name] = value.GetBoolean();
                    break;
                }
            }
        }

        normalized = JsonSerializer.Serialize(result);
        return true;
    }

    /// <summary>Человекочитаемое описание действия для карточки подтверждения и уведомления.</summary>
    public static (string Title, string Details) Describe(string tool, string normalizedArgs)
    {
        using var doc = JsonDocument.Parse(normalizedArgs);
        var a = doc.RootElement;
        string Str(string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? Clean(v.GetString(), 300)
            : string.Empty;
        bool Flag(string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        switch (tool)
        {
            case "phone_uninstall_app":
                return ($"Удалить приложение {Str("package")}", "Приложение и его данные будут удалены с телефона.");
            case "phone_clear_cache":
                return Str("package").Length == 0
                    ? ("Очистить кэш", "Будет очищен кэш всех приложений. Личные данные не затрагиваются.")
                    : ($"Очистить кэш {Str("package")}", "Личные данные приложения не затрагиваются.");
            case "phone_force_stop_app":
                return ($"Остановить приложение {Str("package")}", "Приложение будет принудительно закрыто.");
            case "phone_set_wallpaper":
                var target = Str("target") switch { "home" => "главный экран", "lock" => "экран блокировки", _ => "главный экран и экран блокировки" };
                return ("Поставить обои", $"Фото станет обоями: {target}.");
            case "phone_file_write":
                return (Flag("append") ? $"Дописать в файл {Str("path")}" : $"Записать файл {Str("path")}",
                    "Размер: " + (a.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()!.Length : 0) + " симв.");
            case "phone_file_mkdir":
                return ($"Создать папку {Str("path")}", string.Empty);
            case "phone_file_copy":
                return ($"Копировать файл {Str("from")}", $"Куда: {Str("to")}");
            case "phone_file_move":
                return ($"Переместить файл {Str("from")}", $"Куда: {Str("to")}");
            case "phone_file_delete":
                return ($"Удалить файл {Str("path")}", Flag("recursive") ? "Папка будет удалена со всем содержимым." : "Удаление нельзя отменить.");
            default:
                return ($"Действие {tool} на телефоне", string.Empty);
        }
    }

    private static string Clean(string? v, int max)
    {
        var sb = new StringBuilder();
        foreach (var ch in v ?? string.Empty)
        {
            if (sb.Length >= max)
                break;
            if (!char.IsControl(ch))
                sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
