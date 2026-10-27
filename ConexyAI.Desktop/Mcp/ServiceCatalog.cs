namespace ConexyAI.Desktop.Mcp;

/// <summary>Куда в HTTP-запросе попадает параметр операции.</summary>
public enum SvcIn { Path, Query, Body, Header }

/// <summary>Куда уходят «свободные» поля <c>data</c> (когда у API слишком много методов, чтобы описывать каждый).</summary>
public enum SvcData { None, Query, Json }

/// <param name="Name">Имя параметра в вызове и в запросе.</param>
/// <param name="Description">Подсказка агенту.</param>
/// <param name="In">Путь, query, тело или заголовок.</param>
/// <param name="Required">Обязательный.</param>
/// <param name="Type">string | integer | boolean | number.</param>
public sealed record SvcParam(string Name, string Description, SvcIn In = SvcIn.Query, bool Required = false, string Type = "string");

/// <summary>
/// Операция REST-сервиса. <paramref name="Write"/> — изменяет данные (подтверждает человек). Если в пути есть
/// <c>{method}</c> (универсальный вызов: VK, Telegram, Bitrix24), читающими считаются методы из
/// <paramref name="ReadVerbs"/>, всё остальное — запись.
/// </summary>
public sealed record SvcOp(
    string Name,
    string Description,
    string Method,
    string Path,
    SvcParam[]? Params = null,
    bool Write = false,
    SvcData Data = SvcData.None,
    string[]? ReadVerbs = null,
    string? DataHelp = null);

/// <summary>
/// REST-сервис: базовый адрес, заголовки и query с подстановкой ввода пользователя (<c>${ИМЯ}</c>), набор операций.
/// Всё, что требует ключей, хранится на этом ПК зашифрованно; сервер ConexyAI их не видит.
/// </summary>
public sealed record ServiceDef(
    string Id,
    string Title,
    string Description,
    string BaseUrl,
    SvcOp[] Ops,
    CatalogInput[]? Inputs = null,
    Dictionary<string, string>? Headers = null,
    Dictionary<string, string>? Query = null,
    string? CheckOp = null,
    string? Note = null,
    string Category = "РФ");

/// <summary>
/// Сервисы, к которым агент ходит напрямую по официальным API — без Node.js и сторонних пакетов. Ключ вводится
/// один раз: <c>/connect &lt;id&gt;</c>. Агенту отдаются всего два инструмента на ВСЕ сервисы (список и вызов),
/// поэтому каталог можно наращивать без роста числа инструментов.
/// </summary>
public static class ServiceCatalog
{
    private static readonly string[] GetVerbs = ["get*", "search*", "is*", "check*", "resolve*", "list*", "fetch*"];

    private static CatalogInput Token(string name, string prompt, string help, string? pattern = null) =>
        new(name, prompt, help, Pattern: pattern);

    public static readonly IReadOnlyList<ServiceDef> Services =
    [
        new("yandex_disk", "Яндекс Диск",
            "Файлы и папки на Яндекс Диске: список, поиск по типу, ссылки на скачивание и загрузку, создание папок, копирование, перенос, удаление, публикация.",
            "https://cloud-api.yandex.net/v1/disk",
            Inputs:
            [
                Token("YANDEX_OAUTH_TOKEN", "OAuth-токен Яндекса",
                    "oauth.yandex.ru → создать приложение с правами «Яндекс Диск REST API» (cloud_api:disk.read, .write, .info) → открыть https://oauth.yandex.ru/authorize?response_type=token&client_id=<ID приложения> → токен из адреса после #access_token="),
            ],
            Headers: new() { ["Authorization"] = "OAuth ${YANDEX_OAUTH_TOKEN}" },
            CheckOp: "info",
            Ops:
            [
                new("info", "Сведения о диске: объём, занято, корзина.", "GET", "/"),
                new("list", "Содержимое папки (или сведения о файле). path вида disk:/Документы или /Документы.", "GET", "/resources",
                [
                    new("path", "Путь папки или файла (disk:/…)", Required: true),
                    new("limit", "Сколько элементов (до 1000)", Type: "integer"),
                    new("offset", "Сдвиг для постраничного чтения", Type: "integer"),
                    new("sort", "name, path, created, modified, size (минус — по убыванию)"),
                ]),
                new("files", "Все файлы диска по типу (document, image, video, audio, archive, text, spreadsheet, presentation…) — плоским списком.", "GET", "/resources/files",
                [
                    new("media_type", "Тип файлов через запятую"),
                    new("limit", "Сколько элементов", Type: "integer"),
                    new("offset", "Сдвиг", Type: "integer"),
                ]),
                new("download_url", "Ссылка для скачивания файла (ответ — href; скачайте её командой curl).", "GET", "/resources/download",
                    [new("path", "Путь файла", Required: true)]),
                new("upload_url", "Ссылка для загрузки файла (ответ — href; загрузите на неё методом PUT командой curl).", "GET", "/resources/upload",
                [
                    new("path", "Куда положить на диске", Required: true),
                    new("overwrite", "Перезаписать, если файл есть", Type: "boolean"),
                ]),
                new("mkdir", "Создать папку.", "PUT", "/resources", [new("path", "Путь новой папки", Required: true)], Write: true),
                new("copy", "Копировать файл или папку.", "POST", "/resources/copy",
                [
                    new("from", "Откуда", Required: true),
                    new("path", "Куда", Required: true),
                    new("overwrite", "Перезаписать", Type: "boolean"),
                ], Write: true),
                new("move", "Переместить или переименовать.", "POST", "/resources/move",
                [
                    new("from", "Откуда", Required: true),
                    new("path", "Куда", Required: true),
                    new("overwrite", "Перезаписать", Type: "boolean"),
                ], Write: true),
                new("delete", "Удалить файл или папку (по умолчанию — в корзину).", "DELETE", "/resources",
                [
                    new("path", "Что удалить", Required: true),
                    new("permanently", "Удалить безвозвратно, минуя корзину", Type: "boolean"),
                ], Write: true),
                new("publish", "Открыть публичную ссылку на файл или папку.", "PUT", "/resources/publish",
                    [new("path", "Что публиковать", Required: true)], Write: true),
                new("unpublish", "Закрыть публичную ссылку.", "PUT", "/resources/unpublish",
                    [new("path", "Что закрыть", Required: true)], Write: true),
            ]),

        new("telegram_bot", "Telegram (бот)",
            "Telegram Bot API от имени вашего бота: отправка сообщений и файлов в чаты и каналы, чтение входящих (getUpdates), сведения о чатах.",
            "https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}",
            Inputs:
            [
                Token("TELEGRAM_BOT_TOKEN", "Токен бота",
                    "В Telegram откройте @BotFather → /newbot (или /mybots → API Token). Бот видит только чаты, куда его добавили или где ему написали.",
                    pattern: @"^\d{6,12}:[A-Za-z0-9_-]{30,50}$"),
            ],
            CheckOp: "api",
            Note: "Бот не читает историю чатов и личку пользователя — только то, что адресовано боту. Для канала добавьте бота администратором.",
            Ops:
            [
                new("send_message", "Отправить сообщение в чат, группу или канал.", "POST", "/sendMessage",
                [
                    new("chat_id", "ID чата или @username канала", SvcIn.Body, Required: true),
                    new("text", "Текст (до 4096 символов)", SvcIn.Body, Required: true),
                    new("parse_mode", "HTML или MarkdownV2 (необязательно)", SvcIn.Body),
                    new("reply_to_message_id", "Ответ на сообщение", SvcIn.Body, Type: "integer"),
                ], Write: true),
                new("api", "Любой метод Bot API (getMe, getUpdates, getChat, sendPhoto, sendDocument по ссылке, pinChatMessage …). Имя метода — в method, аргументы — в data.",
                    "POST", "/{method}", [new("method", "Метод Bot API, например getMe", SvcIn.Path, Required: true)],
                    Data: SvcData.Json, ReadVerbs: ["get*"],
                    DataHelp: "Аргументы метода как JSON-объект, например {\"chat_id\": 123, \"text\": \"привет\"}."),
            ]),

        new("vk", "ВКонтакте",
            "API ВКонтакте: стена и посты сообщества, сообщения сообщества, пользователи, группы, фото. Любой метод API по имени (wall.post, messages.send, groups.getById …).",
            "https://api.vk.com/method",
            Inputs:
            [
                Token("VK_ACCESS_TOKEN", "Ключ доступа",
                    "Для сообщества: Управление → Работа с API → Ключи доступа (права: стена, сообщения). Для личных данных — токен VK ID с нужными правами."),
            ],
            Query: new() { ["access_token"] = "${VK_ACCESS_TOKEN}", ["v"] = "5.199" },
            CheckOp: "api",
            Ops:
            [
                new("api", "Метод VK API по имени, например users.get, wall.get, wall.post, messages.send, groups.getById. Аргументы — в data (десятичные числа и строки).",
                    "POST", "/{method}", [new("method", "Метод API, например wall.get", SvcIn.Path, Required: true)],
                    Data: SvcData.Query, ReadVerbs: GetVerbs,
                    DataHelp: "Параметры метода как JSON-объект, например {\"owner_id\": -1, \"count\": 5}. Для messages.send добавьте random_id."),
            ]),

        new("bitrix24", "Битрикс24",
            "CRM и задачи Битрикс24 через входящий вебхук: лиды, сделки, контакты, компании, задачи, пользователи, чаты. Любой метод REST по имени (crm.deal.list, tasks.task.add …).",
            "${B24_BASE}",
            Inputs:
            [
                Token("B24_BASE", "URL входящего вебхука",
                    "Битрикс24 → Разработчикам → Другое → Входящий вебхук → выберите права (crm, task, user) → скопируйте «URL для вызова REST». Вид: https://компания.bitrix24.ru/rest/1/код/",
                    pattern: @"^https://[A-Za-z0-9.-]+\.[A-Za-z]{2,}/rest/\d+/[A-Za-z0-9]+/?$"),
            ],
            CheckOp: "api",
            Note: "Права вебхука задаёте вы — выдайте только нужные (например, crm без удаления).",
            Ops:
            [
                new("api", "Метод REST Битрикс24 по имени, например crm.lead.list, crm.deal.add, tasks.task.list, user.get. Аргументы — в data.",
                    "POST", "/{method}.json", [new("method", "Метод, например crm.deal.list", SvcIn.Path, Required: true)],
                    Data: SvcData.Json,
                    ReadVerbs: ["get", "list", "fields", "current", "search", "count", "info", "types", "status", "scope", "methods", "profile"],
                    DataHelp: "Тело запроса как JSON, например {\"select\": [\"ID\",\"TITLE\"], \"filter\": {\">OPPORTUNITY\": 10000}}."),
            ]),

        new("yandex_tracker", "Яндекс Трекер",
            "Задачи Яндекс Трекера: поиск, чтение, создание и обновление задач, комментарии, переходы по статусам, очереди.",
            "https://api.tracker.yandex.net",
            Inputs:
            [
                Token("TRACKER_OAUTH_TOKEN", "OAuth-токен Трекера",
                    "oauth.yandex.ru → приложение с доступом «Яндекс Трекер» (tracker:read, tracker:write) → токен через https://oauth.yandex.ru/authorize?response_type=token&client_id=<ID>."),
                new("TRACKER_ORG_ID", "ID организации Яндекс 360 (если у вас Cloud Organization — Enter)",
                    "Трекер → Администрирование → Организации: числовой ID. Заполните РОВНО ОДНО из двух полей: ID или Cloud ID.", Secret: false, Optional: true),
                new("TRACKER_CLOUD_ORG_ID", "ID Yandex Cloud Organization (если у вас Яндекс 360 — Enter)",
                    "Буквенно-цифровой ID организации в Yandex Cloud Organization.", Secret: false, Optional: true),
            ],
            // Пустой заголовок пропускается: у одной организации заполнен ровно один.
            Headers: new()
            {
                ["Authorization"] = "OAuth ${TRACKER_OAUTH_TOKEN}",
                ["X-Org-ID"] = "${TRACKER_ORG_ID}",
                ["X-Cloud-Org-ID"] = "${TRACKER_CLOUD_ORG_ID}",
            },
            CheckOp: "myself",
            Ops:
            [
                new("myself", "Кто я в Трекере.", "GET", "/v2/myself"),
                new("queues", "Список очередей.", "GET", "/v2/queues", [new("perPage", "Сколько очередей", Type: "integer")]),
                new("search", "Поиск задач. query — на языке запросов Трекера (Queue: PROJ AND Status: Open), либо filter в data.", "POST", "/v2/issues/_search",
                [
                    new("query", "Запрос Трекера", SvcIn.Body),
                    new("perPage", "Задач на страницу", SvcIn.Query, Type: "integer"),
                ], Data: SvcData.Json, DataHelp: "Дополнительно: {\"filter\": {\"queue\": \"PROJ\"}, \"order\": \"-updated\"}."),
                new("get", "Задача по ключу (PROJ-123).", "GET", "/v2/issues/{key}", [new("key", "Ключ задачи", SvcIn.Path, Required: true)]),
                new("create", "Создать задачу.", "POST", "/v2/issues",
                [
                    new("queue", "Ключ очереди", SvcIn.Body, Required: true),
                    new("summary", "Название", SvcIn.Body, Required: true),
                    new("description", "Описание", SvcIn.Body),
                    new("assignee", "Логин исполнителя", SvcIn.Body),
                    new("type", "Тип: bug, task …", SvcIn.Body),
                    new("priority", "Приоритет", SvcIn.Body),
                ], Write: true, Data: SvcData.Json),
                new("update", "Изменить задачу: поля — в data.", "PATCH", "/v2/issues/{key}", [new("key", "Ключ задачи", SvcIn.Path, Required: true)],
                    Write: true, Data: SvcData.Json, DataHelp: "Поля задачи, например {\"summary\": \"Новое название\", \"assignee\": \"login\"}."),
                new("comment", "Добавить комментарий.", "POST", "/v2/issues/{key}/comments",
                [
                    new("key", "Ключ задачи", SvcIn.Path, Required: true),
                    new("text", "Текст комментария", SvcIn.Body, Required: true),
                ], Write: true),
                new("transitions", "Доступные переходы по статусам.", "GET", "/v2/issues/{key}/transitions", [new("key", "Ключ задачи", SvcIn.Path, Required: true)]),
                new("transition", "Выполнить переход по статусу.", "POST", "/v2/issues/{key}/transitions/{transition}/_execute",
                [
                    new("key", "Ключ задачи", SvcIn.Path, Required: true),
                    new("transition", "id перехода из transitions", SvcIn.Path, Required: true),
                ], Write: true, Data: SvcData.Json),
            ]),

        new("yandex_metrika", "Яндекс Метрика",
            "Статистика сайтов: список счётчиков, отчёты по посещаемости, источникам, страницам, целям — любые метрики и срезы Метрики.",
            "https://api-metrika.yandex.net",
            Inputs:
            [
                Token("YANDEX_OAUTH_TOKEN", "OAuth-токен Яндекса",
                    "oauth.yandex.ru → приложение с доступом «Яндекс Метрика» (metrika:read) → токен через https://oauth.yandex.ru/authorize?response_type=token&client_id=<ID>."),
            ],
            Headers: new() { ["Authorization"] = "OAuth ${YANDEX_OAUTH_TOKEN}" },
            CheckOp: "counters",
            Ops:
            [
                new("counters", "Счётчики аккаунта.", "GET", "/management/v1/counters", [new("per_page", "Сколько счётчиков", Type: "integer")]),
                new("report", "Табличный отчёт. Например metrics=ym:s:visits,ym:s:users; dimensions=ym:s:trafficSourceName; date1=7daysAgo; date2=today.", "GET", "/stat/v1/data",
                [
                    new("ids", "id счётчика (через запятую)", Required: true),
                    new("metrics", "Метрики через запятую (ym:s:visits …)", Required: true),
                    new("dimensions", "Группировки через запятую"),
                    new("date1", "Начало: YYYY-MM-DD или 7daysAgo"),
                    new("date2", "Конец: YYYY-MM-DD или today"),
                    new("filters", "Фильтр Метрики"),
                    new("sort", "Сортировка"),
                    new("limit", "Строк", Type: "integer"),
                ]),
            ]),

        new("dadata", "DaData",
            "Данные о компаниях и адресах РФ: проверка контрагента по ИНН/ОГРН (ЕГРЮЛ), подсказки адресов, ФИАС, банки по БИК, геокодирование.",
            "https://suggestions.dadata.ru/suggestions/api/4_1/rs",
            Inputs:
            [
                Token("DADATA_API_KEY", "API-ключ DaData", "dadata.ru → Профиль → API-ключ (на бесплатном тарифе — 10 000 запросов в сутки)."),
            ],
            Headers: new() { ["Authorization"] = "Token ${DADATA_API_KEY}" },
            CheckOp: "party_by_inn",
            Ops:
            [
                new("party_by_inn", "Организация или ИП по ИНН/ОГРН: название, адрес, руководитель, статус, ОКВЭД, финансы.", "POST", "/findById/party",
                [
                    new("query", "ИНН или ОГРН", SvcIn.Body, Required: true),
                    new("kpp", "КПП (если у компании несколько филиалов)", SvcIn.Body),
                ]),
                new("suggest_party", "Поиск организаций по названию или части ИНН.", "POST", "/suggest/party",
                [
                    new("query", "Название или ИНН", SvcIn.Body, Required: true),
                    new("count", "Сколько вариантов (до 20)", SvcIn.Body, Type: "integer"),
                ]),
                new("suggest_address", "Подсказки и разбор адреса: индекс, ФИАС, координаты.", "POST", "/suggest/address",
                [
                    new("query", "Адрес в свободной форме", SvcIn.Body, Required: true),
                    new("count", "Сколько вариантов (до 20)", SvcIn.Body, Type: "integer"),
                ]),
                new("bank_by_bik", "Банк по БИК или названию: реквизиты, корсчёт.", "POST", "/suggest/bank",
                [
                    new("query", "БИК, SWIFT, ИНН или название", SvcIn.Body, Required: true),
                    new("count", "Сколько вариантов", SvcIn.Body, Type: "integer"),
                ]),
                new("geocode", "Адрес по координатам.", "POST", "/geolocate/address",
                [
                    new("lat", "Широта", SvcIn.Body, Required: true, Type: "number"),
                    new("lon", "Долгота", SvcIn.Body, Required: true, Type: "number"),
                ]),
            ]),

        new("moysklad", "МойСклад",
            "Товары, остатки, контрагенты, заказы и отгрузки в МойСкладе (JSON API 1.2): чтение и создание документов.",
            "https://api.moysklad.ru/api/remap/1.2",
            Inputs:
            [
                Token("MOYSKLAD_TOKEN", "Токен МоегоСклада", "Аккаунт → Настройки → Безопасность → Токены (или Настройки → Токены API) → создать."),
            ],
            // gzip требует сам сервис: HttpClient добавляет Accept-Encoding и распаковывает ответ сам.
            Headers: new() { ["Authorization"] = "Bearer ${MOYSKLAD_TOKEN}" },
            CheckOp: "context",
            Ops:
            [
                new("context", "Сведения об аккаунте и сотруднике.", "GET", "/context/employee"),
                new("list", "Список сущностей: product, counterparty, customerorder, demand, supply, store, organization … (type). Фильтры — search, filter, order, limit в параметрах.", "GET", "/entity/{type}",
                [
                    new("type", "Тип сущности, например product или customerorder", SvcIn.Path, Required: true),
                    new("search", "Поиск по строке"),
                    new("filter", "Фильтр: name~хлеб;archived=false"),
                    new("order", "Сортировка: name,asc"),
                    new("limit", "Сколько (до 1000)", Type: "integer"),
                    new("offset", "Сдвиг", Type: "integer"),
                ]),
                new("get", "Одна сущность по id.", "GET", "/entity/{type}/{id}",
                [
                    new("type", "Тип сущности", SvcIn.Path, Required: true),
                    new("id", "UUID сущности", SvcIn.Path, Required: true),
                ]),
                new("stock", "Остатки по товарам.", "GET", "/report/stock/all",
                [
                    new("limit", "Сколько", Type: "integer"),
                    new("filter", "Фильтр"),
                ]),
                new("create", "Создать сущность или документ (тело — в data).", "POST", "/entity/{type}",
                    [new("type", "Тип сущности", SvcIn.Path, Required: true)], Write: true, Data: SvcData.Json,
                    DataHelp: "Тело как в документации МоегоСклада, например {\"name\": \"Новый товар\"}."),
            ]),

        new("amocrm", "amoCRM (Kommo)",
            "Сделки, контакты, компании, задачи и воронки amoCRM (API v4): чтение и создание.",
            "https://${AMOCRM_SUBDOMAIN}.amocrm.ru/api/v4",
            Inputs:
            [
                new("AMOCRM_SUBDOMAIN", "Поддомен аккаунта", "Адрес вида https://ПОДДОМЕН.amocrm.ru — нужна только часть до .amocrm.ru.",
                    Secret: false, Pattern: "^[A-Za-z0-9-]{2,63}$"),
                Token("AMOCRM_TOKEN", "Долгосрочный токен",
                    "amoCRM → Настройки → Интеграции → создать внешнюю интеграцию → вкладка «Ключи и доступы» → «Долгосрочный токен»."),
            ],
            Headers: new() { ["Authorization"] = "Bearer ${AMOCRM_TOKEN}" },
            CheckOp: "account",
            Ops:
            [
                new("account", "Сведения об аккаунте.", "GET", "/account"),
                new("list", "Список сущностей: leads, contacts, companies, tasks, leads/pipelines, users, catalogs …", "GET", "/{entity}",
                [
                    new("entity", "leads, contacts, companies, tasks, leads/pipelines, users", SvcIn.Path, Required: true),
                    new("query", "Поиск по строке"),
                    new("limit", "Сколько (до 250)", Type: "integer"),
                    new("page", "Страница", Type: "integer"),
                ]),
                new("get", "Одна сущность по id.", "GET", "/{entity}/{id}",
                [
                    new("entity", "leads, contacts, companies …", SvcIn.Path, Required: true),
                    new("id", "id", SvcIn.Path, Required: true),
                ]),
                new("create", "Создать сущности пачкой (тело — массив в data).", "POST", "/{entity}",
                    [new("entity", "leads, contacts, companies, tasks", SvcIn.Path, Required: true)], Write: true, Data: SvcData.Json,
                    DataHelp: "Массив объектов, например [{\"name\": \"Сделка\", \"price\": 50000}]."),
                new("update", "Изменить сущности пачкой (массив в data, у каждой — id).", "PATCH", "/{entity}",
                    [new("entity", "leads, contacts, companies, tasks", SvcIn.Path, Required: true)], Write: true, Data: SvcData.Json,
                    DataHelp: "Массив объектов с id, например [{\"id\": 1, \"price\": 60000}]."),
            ]),

        new("yookassa", "ЮKassa (чтение)",
            "Платежи и возвраты магазина в ЮKassa — только чтение: список и состояние платежей, возвраты. Деньги агент не двигает.",
            "https://api.yookassa.ru/v3",
            Inputs:
            [
                new("YOOKASSA_SHOP_ID", "shopId магазина", "Личный кабинет ЮKassa → Интеграция → Ключи API.", Secret: false),
                Token("YOOKASSA_SECRET", "Секретный ключ", "Там же: «Выпустить ключ» (для чтения достаточно ключа с правом на просмотр)."),
            ],
            Headers: new() { ["Authorization"] = "Basic ${b64:${YOOKASSA_SHOP_ID}:${YOOKASSA_SECRET}}" },
            CheckOp: "payments",
            Note: "Создание платежей и возвратов не включено намеренно.",
            Ops:
            [
                new("payments", "Список платежей с фильтрами.", "GET", "/payments",
                [
                    new("status", "pending, waiting_for_capture, succeeded, canceled"),
                    new("created_at.gte", "С момента (ISO 8601)"),
                    new("created_at.lte", "По момент (ISO 8601)"),
                    new("limit", "Сколько (до 100)", Type: "integer"),
                    new("cursor", "Курсор следующей страницы"),
                ]),
                new("payment", "Платёж по id.", "GET", "/payments/{id}", [new("id", "id платежа", SvcIn.Path, Required: true)]),
                new("refunds", "Список возвратов.", "GET", "/refunds",
                [
                    new("payment_id", "Только по этому платежу"),
                    new("limit", "Сколько", Type: "integer"),
                    new("cursor", "Курсор следующей страницы"),
                ]),
                new("refund", "Возврат по id.", "GET", "/refunds/{id}", [new("id", "id возврата", SvcIn.Path, Required: true)]),
            ]),

        new("cbr", "Курсы ЦБ РФ",
            "Официальные курсы валют ЦБ РФ на сегодня и на любую дату. Ключ не нужен.",
            "https://www.cbr-xml-daily.ru",
            Note: "Данные ЦБ в JSON через общедоступное зеркало cbr-xml-daily.ru.",
            Ops:
            [
                new("rates", "Курсы всех валют на сегодня (Valute.USD.Value — рублей за Nominal единиц).", "GET", "/daily_json.js"),
                new("rates_on", "Курсы на дату (последний рабочий день до неё).", "GET", "/archive/{year}/{month}/{day}/daily_json.js",
                [
                    new("year", "Год, 4 цифры", SvcIn.Path, Required: true),
                    new("month", "Месяц, 2 цифры", SvcIn.Path, Required: true),
                    new("day", "День, 2 цифры", SvcIn.Path, Required: true),
                ]),
            ]),

        new("max", "MAX (бот)",
            "Мессенджер MAX от имени вашего бота: отправка сообщений в чаты и каналы, список чатов, чтение входящих.",
            "https://platform-api.max.ru",
            Inputs:
            [
                Token("MAX_BOT_TOKEN", "Токен бота",
                    "MAX для партнёров (business.max.ru) → Чат-боты → создать бота → Интеграция → получить токен. Бот видит только чаты, куда его добавили."),
            ],
            Headers: new() { ["Authorization"] = "${MAX_BOT_TOKEN}" },
            CheckOp: "me",
            Ops:
            [
                new("me", "Сведения о боте.", "GET", "/me"),
                new("chats", "Список чатов бота.", "GET", "/chats",
                [
                    new("count", "Сколько (до 100)", Type: "integer"),
                    new("marker", "Маркер следующей страницы", Type: "integer"),
                ]),
                new("messages", "Сообщения чата.", "GET", "/messages",
                [
                    new("chat_id", "ID чата", Required: true, Type: "integer"),
                    new("count", "Сколько (до 100)", Type: "integer"),
                ]),
                new("updates", "Новые события для бота (входящие сообщения).", "GET", "/updates",
                [
                    new("limit", "Сколько событий", Type: "integer"),
                    new("marker", "Маркер, с которого читать", Type: "integer"),
                ]),
                new("send_message", "Отправить сообщение в чат.", "POST", "/messages",
                [
                    new("chat_id", "ID чата", Required: true, Type: "integer"),
                    new("text", "Текст (до 4000 символов)", SvcIn.Body, Required: true),
                ], Write: true),
            ]),

        new("yandex_360", "Яндекс 360 для бизнеса",
            "Администрирование организации в Яндекс 360: сотрудники, подразделения, группы.",
            "https://api360.yandex.net/directory/v1/org/${Y360_ORG_ID}",
            Inputs:
            [
                new("Y360_ORG_ID", "ID организации", "admin.yandex.ru → Настройки → Профиль компании (ID организации).",
                    Secret: false, Pattern: @"^\d{1,15}$"),
                Token("Y360_TOKEN", "OAuth-токен API 360",
                    "oauth.yandex.ru → приложение с правами directory:read_users, directory:read_departments, directory:read_groups → токен администратора организации."),
            ],
            Headers: new() { ["Authorization"] = "OAuth ${Y360_TOKEN}" },
            CheckOp: "users",
            Note: "Только чтение: управлять сотрудниками агент не может.",
            Ops:
            [
                new("users", "Сотрудники организации.", "GET", "/users",
                [
                    new("page", "Страница", Type: "integer"),
                    new("perPage", "Сколько на странице (до 1000)", Type: "integer"),
                ]),
                new("departments", "Подразделения.", "GET", "/departments",
                [
                    new("page", "Страница", Type: "integer"),
                    new("perPage", "Сколько на странице", Type: "integer"),
                ]),
                new("groups", "Группы.", "GET", "/groups",
                [
                    new("page", "Страница", Type: "integer"),
                    new("perPage", "Сколько на странице", Type: "integer"),
                ]),
            ]),

        new("ozon_seller", "Ozon / Озон (продавец)",
            "Seller API Ozon: товары, остатки, заказы FBS/FBO, финансы. Любой метод по пути (api).",
            "https://api-seller.ozon.ru",
            Inputs:
            [
                new("OZON_CLIENT_ID", "Client-Id", "seller.ozon.ru → Настройки → API-ключи (Client ID).", Secret: false, Pattern: @"^\d{1,12}$"),
                Token("OZON_API_KEY", "Api-Key", "Там же: «Сгенерировать ключ» (роль — только чтение, если не нужны изменения)."),
            ],
            Headers: new() { ["Client-Id"] = "${OZON_CLIENT_ID}", ["Api-Key"] = "${OZON_API_KEY}" },
            CheckOp: "products",
            Note: "Ключу с ролью «только чтение» изменения недоступны. Прямой вызов api всегда требует подтверждения.",
            Ops:
            [
                new("products", "Список товаров (v3/product/list). Фильтры — в data, например {\"filter\":{\"visibility\":\"ALL\"},\"limit\":100}.",
                    "POST", "/v3/product/list", Data: SvcData.Json,
                    DataHelp: "{\"filter\": {\"visibility\": \"ALL\"}, \"last_id\": \"\", \"limit\": 100}"),
                new("stocks", "Остатки товаров (v4/product/info/stocks).", "POST", "/v4/product/info/stocks", Data: SvcData.Json,
                    DataHelp: "{\"filter\": {\"visibility\": \"ALL\"}, \"limit\": 100}"),
                new("fbs_orders", "Отправления FBS за период (v3/posting/fbs/list).", "POST", "/v3/posting/fbs/list", Data: SvcData.Json,
                    DataHelp: "{\"dir\": \"DESC\", \"filter\": {\"since\": \"2025-01-01T00:00:00Z\", \"to\": \"2025-01-31T23:59:59Z\"}, \"limit\": 50, \"offset\": 0}"),
                new("finance_totals", "Итоги начислений за период (v3/finance/transaction/totals).", "POST", "/v3/finance/transaction/totals", Data: SvcData.Json,
                    DataHelp: "{\"date\": {\"from\": \"2025-01-01T00:00:00Z\", \"to\": \"2025-01-31T23:59:59Z\"}}"),
                new("api", "Любой метод Seller API по пути, например v1/analytics/data.", "POST", "/{endpoint}",
                    [new("endpoint", "Путь метода без начального «/», например v3/product/info/list", SvcIn.Path, Required: true)],
                    Write: true, Data: SvcData.Json, DataHelp: "Тело запроса как в документации Ozon Seller API."),
            ]),

        new("yandex_market", "Яндекс Маркет (партнёр)",
            "Partner API Яндекс Маркета: кампании магазина и каталог предложений.",
            "https://api.partner.market.yandex.ru",
            Inputs:
            [
                Token("YM_API_KEY", "Api-Key", "partner.market.yandex.ru → Настройки → API и модули → Токены API (права — только чтение)."),
            ],
            Headers: new() { ["Api-Key"] = "${YM_API_KEY}" },
            CheckOp: "campaigns",
            Ops:
            [
                new("campaigns", "Кампании (магазины) аккаунта.", "GET", "/campaigns"),
                new("offers", "Карточки товаров бизнеса (offer-mappings).", "POST", "/businesses/{business_id}/offer-mappings",
                [
                    new("business_id", "ID бизнеса", SvcIn.Path, Required: true),
                    new("limit", "Сколько (до 200)", Type: "integer"),
                    new("page_token", "Токен следующей страницы"),
                ], Data: SvcData.Json, DataHelp: "Фильтры, например {\"offerIds\": [\"sku-1\"]}; можно пустой объект {}."),
            ]),

        new("kontur_focus", "Контур.Фокус",
            "Проверка контрагентов: реквизиты, ЕГРЮЛ/ЕГРИП, бухгалтерская отчётность по ИНН или ОГРН.",
            "https://focus-api.kontur.ru/api3",
            Inputs:
            [
                Token("FOCUS_KEY", "Ключ API Фокуса", "focus.kontur.ru → Интеграция → API → ключ (платный тариф с API)."),
            ],
            Query: new() { ["key"] = "${FOCUS_KEY}" },
            Ops:
            [
                new("req", "Реквизиты и статус компании или ИП.", "GET", "/req",
                [
                    new("inn", "ИНН (через запятую — до 100)"),
                    new("ogrn", "ОГРН"),
                ]),
                new("egr_details", "Сведения из ЕГРЮЛ/ЕГРИП.", "GET", "/egrDetails",
                [
                    new("inn", "ИНН"),
                    new("ogrn", "ОГРН"),
                ]),
                new("buh", "Бухгалтерская отчётность.", "GET", "/buh",
                [
                    new("inn", "ИНН"),
                    new("ogrn", "ОГРН"),
                ]),
            ]),

        new("sms_ru", "SMS.ru",
            "Отправка SMS и проверка баланса через SMS.ru.",
            "https://sms.ru",
            Inputs:
            [
                Token("SMSRU_API_ID", "api_id", "sms.ru → Личный кабинет → Отправка SMS → api_id (в настройках можно ограничить по IP)."),
            ],
            Query: new() { ["api_id"] = "${SMSRU_API_ID}", ["json"] = "1" },
            CheckOp: "balance",
            Note: "Отправка SMS стоит денег и всегда требует подтверждения.",
            Ops:
            [
                new("balance", "Баланс.", "GET", "/my/balance"),
                new("cost", "Стоимость сообщения.", "GET", "/sms/cost",
                [
                    new("to", "Номер, 79991234567", Required: true),
                    new("msg", "Текст", Required: true),
                ]),
                new("status", "Статус отправленного SMS.", "GET", "/sms/status", [new("sms_id", "id из ответа send", Required: true)]),
                new("send", "Отправить SMS.", "GET", "/sms/send",
                [
                    new("to", "Номер, 79991234567 (через запятую — несколько)", Required: true),
                    new("msg", "Текст", Required: true),
                ], Write: true),
            ]),

        new("unisender", "UniSender",
            "Email- и SMS-рассылки: списки, контакты, кампании, статистика. Любой метод API по имени.",
            "https://api.unisender.com/ru/api",
            Inputs:
            [
                Token("UNISENDER_API_KEY", "API-ключ", "UniSender → Настройки → Интеграция и API → ключ API."),
            ],
            Query: new() { ["api_key"] = "${UNISENDER_API_KEY}", ["format"] = "json" },
            CheckOp: "api",
            Note: "Рассылки, импорт и удаление контактов считаются записью и подтверждаются человеком.",
            Ops:
            [
                new("api", "Метод API по имени: getLists, getCampaigns, getCampaignCommonStats, importContacts, createEmailMessage …",
                    "POST", "/{method}", [new("method", "Метод, например getLists", SvcIn.Path, Required: true)],
                    Data: SvcData.Query, ReadVerbs: GetVerbs,
                    DataHelp: "Параметры метода как JSON-объект, например {\"list_ids\": \"1\"}."),
            ]),

        new("two_gis", "2ГИС",
            "Поиск организаций, адресов и мест в 2ГИС: телефоны, часы работы, координаты, рейтинг.",
            "https://catalog.api.2gis.com/3.0",
            Inputs:
            [
                Token("TWOGIS_KEY", "Ключ API 2ГИС", "dev.2gis.ru → Личный кабинет → создать демо-ключ (бесплатно) или платный."),
            ],
            Query: new() { ["key"] = "${TWOGIS_KEY}" },
            CheckOp: "search",
            Ops:
            [
                new("search", "Поиск организаций и объектов.", "GET", "/items",
                [
                    new("q", "Запрос: «аптека», «кофейня Тверская»", Required: true),
                    new("city_id", "id города из справочника 2ГИС"),
                    new("point", "Центр поиска: lon,lat"),
                    new("radius", "Радиус в метрах", Type: "integer"),
                    new("fields", "Поля: items.point,items.contact_groups,items.schedule,items.rubrics"),
                    new("page_size", "Сколько (до 50)", Type: "integer"),
                ]),
                new("geocode", "Адрес ↔ координаты.", "GET", "/items/geocode",
                [
                    new("q", "Адрес"),
                    new("lon", "Долгота (обратный геокодинг)"),
                    new("lat", "Широта"),
                    new("fields", "Дополнительные поля, например items.point"),
                ]),
            ]),

        new("yandex_geocoder", "Яндекс Геокодер",
            "Адрес в координаты и обратно по данным Яндекс Карт.",
            "https://geocode-maps.yandex.ru/v1",
            Inputs:
            [
                Token("YGEO_KEY", "Ключ Геокодера", "developer.tech.yandex.ru → Кабинет разработчика → получить ключ для «HTTP Геокодер»."),
            ],
            Query: new() { ["apikey"] = "${YGEO_KEY}", ["format"] = "json", ["lang"] = "ru_RU" },
            CheckOp: "geocode",
            Ops:
            [
                new("geocode", "Адрес или «долгота,широта» → объекты с координатами и полным адресом.", "GET", "/",
                [
                    new("geocode", "Адрес или координаты lon,lat", Required: true),
                    new("results", "Сколько вариантов", Type: "integer"),
                ]),
            ]),
    ];

    public static ServiceDef? Find(string id) =>
        Services.FirstOrDefault(s => s.Id.Equals(id.Replace('-', '_'), StringComparison.OrdinalIgnoreCase));
}
