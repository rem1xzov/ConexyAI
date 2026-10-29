# Агент телефона (Android)

В приложении агент работает не только в песочнице и на ПК, но и с самим телефоном: галерея, виджеты,
настройки, приложения, батарея, кэш, файлы, экран (через службу специальных возможностей).
Сервер ведёт агентский цикл, а инструменты `phone_*` выполняет нативный плагин `PhoneAgent`
в приложении — по тому же принципу, что и ПК (`DeviceToolBridge`), но отдельным мостом
(`PhoneToolBridge`), чтобы телефон и ПК не вытесняли друг друга.

```
агент (сервер) ──PhoneTool──▶ WebView (phoneAgent.ts) ──execute──▶ PhoneAgentPlugin (Java)
             ◀─PhoneToolResult──                     ◀──{output,isError}──
```

## Контракт хаба `/hubs/conexy`

Телефон → сервер:

| Метод | Аргументы | Ответ |
|---|---|---|
| `RegisterPhone` | `infoJson: string` — `{"model","manufacturer","androidVersion","sdk","appVersion","permissions":{"<id>":"granted"\|"denied"\|"prompt"\|"unavailable"}}` | `bool` |
| `SetPhoneForeground` | `foreground: bool` | — |
| `PhoneToolResult` | `callId: string, output: string, isError: bool` | `bool` |

Сервер → телефон: событие `PhoneTool` с объектом `{ callId, taskId, tool, arguments }`, где
`arguments` — JSON-строка аргументов инструмента. Телефон обязан ответить `PhoneToolResult`
с тем же `callId`. `RegisterPhone` повторяется после каждого (пере)подключения и после смены
разрешений.

Картинки (скриншот, фото из галереи) возвращаются строкой
`IMAGE_BASE64:<mime>:<base64>[\nзаметка]` — сервер прикладывает их модели как изображение.

## REST

- `POST /api/mobile/push-token` `{ "token": "...", "platform": "android" }` — FCM-токен устройства.
- `DELETE /api/mobile/push-token` `{ "token": "..." }` — при выходе из аккаунта.
- `POST /api/mobile/actions/{actionId}` `{ "approved": true|false }` — решение по подтверждению
  прямо из уведомления (кнопки «Разрешить»/«Отклонить»), JWT в `Authorization: Bearer`.

## Запрос задачи

`ConexyRequest.usePhone = true` — приложение шлёт его, когда плагин `PhoneAgent` есть.
Для Coder/Cowork инструменты телефона добавляются к обычным; для чата (вкладка «Чат», не
«Ученики») ход идёт через агентский цикл с профилем `phone`.

## Push-уведомления (FCM, data-сообщения)

| `type` | Поля | Что показывает телефон |
|---|---|---|
| `confirm` | `actionId, taskId, chatId, title, body, dangerous` | уведомление с кнопками «Разрешить» / «Отклонить» (работают без открытия приложения) |
| `completed` | `taskId, chatId, title, body` | «Задача готова», тап открывает чат |
| `phone_needed` | `taskId, chatId, title, body` | «Агенту нужен телефон — откройте приложение» |
| `dismiss` | `actionId` | ничего не показывает; убирает уведомление `confirm` с этим `actionId` |

Сервер шлёт `completed` только когда приложение не на экране (`SetPhoneForeground(false)`
или телефон не подключён). `confirm` шлётся всегда, кроме случая, когда приложение на экране.

`dismiss` сервер шлёт после `confirm` (в этом порядке), когда решение принято **не из уведомления** — в вебе или
приложении через хаб (`ConfirmAction`), по таймауту (10 мин) или при остановке задачи. Он уходит только если
`confirm` был отправлен и FCM принял его хотя бы на одно устройство (есть зарегистрированные токены; приложение
было не на экране). Решение из самого уведомления (`POST /api/mobile/actions/{actionId}`) `dismiss` не вызывает —
уведомление закрывает клиент. Payload: `{type:"dismiss", actionId}`, других полей нет.

## Инструменты

`R` — только чтение, `C` — сервер спрашивает подтверждение пользователя (карточка в чате +
push с кнопками), `D` — опасное (красная карточка).

| Инструмент | Аргументы | |
|---|---|---|
| `phone_device_info` | — | R |
| `phone_permissions` | — | R |
| `phone_request_permission` | `permission` | R |
| `phone_apps_list` | `filter?: all\|user\|system`, `sort?: last_used\|size\|name\|foreground_time`, `limit?` | R |
| `phone_battery_report` | — | R |
| `phone_storage_report` | — | R |
| `phone_open_settings` | `screen`, `package?` | R |
| `phone_launch_app` | `package` | R |
| `phone_open_url` | `url` | R |
| `phone_uninstall_app` | `package` | C D |
| `phone_clear_cache` | `package?` | C |
| `phone_force_stop_app` | `package` | C |
| `phone_screen_read` | — | R |
| `phone_screenshot` | — | R |
| `phone_tap` | `text?`, `node_id?`, `x?`, `y?`, `long_press?` | R |
| `phone_swipe` | `direction?`, `x1?,y1?,x2?,y2?`, `duration_ms?` | R |
| `phone_type_text` | `text`, `node_id?` | R |
| `phone_global_action` | `action: back\|home\|recents\|notifications\|quick_settings\|lock_screen` | R |
| `phone_gallery_list` | `media?: images\|videos`, `limit?` | R |
| `phone_gallery_pick` | `media?`, `multiple?` | R |
| `phone_image_view` | `uri` | R |
| `phone_widget_photo` | `uri`, `caption?` | R |
| `phone_set_wallpaper` | `uri`, `target?: home\|lock\|both` | C |
| `phone_file_list` | `path?`, `show_hidden?` | R |
| `phone_file_read` | `path`, `offset?`, `limit?` | R |
| `phone_file_search` | `path?`, `pattern?`, `contains?`, `limit?` | R |
| `phone_file_write` | `path`, `content`, `append?` | C |
| `phone_file_mkdir` | `path` | C |
| `phone_file_copy` | `from`, `to` | C |
| `phone_file_move` | `from`, `to` | C |
| `phone_file_delete` | `path`, `recursive?` | C D |
| `phone_share` | `path?`, `text?` | R |
| `phone_clipboard_set` | `text` | R |

Значения `permission`: `camera, microphone, photos, videos, audio, notifications, contacts,
location, usage_access, all_files, accessibility, battery_optimization, install_packages,
overlay, write_settings`.

Значения `screen` для `phone_open_settings`: `app_details, battery, battery_optimization,
storage, usage_access, accessibility, notifications, app_notifications, wifi, bluetooth,
display, sound, location, developer, all_files, default_apps, date, home, apps`.

Относительные пути файлов считаются от рабочей папки агента
`/storage/emulated/0/Documents/ConexyAI` (при доступе «ко всем файлам»), иначе от
приложения-специфичной папки `Android/data/conexy.app/files/workspace`.

## Что нужно для push

1. Проект Firebase с Android-приложением `conexy.app`, файл `google-services.json` →
   `frontend/android/app/google-services.json` (в git не кладём).
2. На сервере: `Push:Fcm:ProjectId` и `Push:Fcm:ServiceAccountJson` (содержимое JSON ключа
   сервисного аккаунта или путь к файлу) — переменные окружения
   `Push__Fcm__ProjectId`, `Push__Fcm__ServiceAccountJson`.

Без них всё работает, кроме уведомлений при закрытом приложении: пока приложение живо,
уведомления показываются локально.

## JS-API нативного плагина `PhoneAgent` (Capacitor, `registerPlugin('PhoneAgent')`)

Java-класс `conexy.app.PhoneAgentPlugin` (`@CapacitorPlugin(name = "PhoneAgent")`), регистрируется
в `MainActivity` рядом с `SpeechInputPlugin`/`ApkUpdaterPlugin`.

| Метод | Аргументы | Результат |
|---|---|---|
| `execute` | `{ tool: string, args: string }` (`args` — JSON-строка) | `{ output: string, isError: boolean }` — выполняет `phone_*` инструмент. Никогда не reject-ит: ошибки — `isError: true` |
| `info` | — | `{ model, manufacturer, androidVersion, sdk, appVersion }` |
| `permissions` | — | `{ permissions: { "<id>": "granted"\|"denied"\|"prompt"\|"unavailable" } }` |
| `requestPermission` | `{ permission: string }` | `{ state: string }` — показывает системный диалог или открывает нужный экран настроек и ждёт возврата в приложение |
| `requestAll` | — | `{ permissions: {...} }` — последовательно запрашивает всё недостающее (runtime-диалоги, затем спец-доступы экранами настроек) |
| `setAuth` | `{ token: string \| null, baseUrl: string }` | `{}` — JWT и адрес сервера для нативных кнопок уведомления (`POST /api/mobile/actions/{id}`). `null` — выход из аккаунта |
| `getPushToken` | — | `{ token: string \| null }` — FCM-токен (null, если Firebase не настроен) |
| `showNotification` | `{ id?: number, title, body, type?: "confirm"\|"completed"\|"phone_needed", actionId?, taskId?, chatId?, dangerous? }` | `{}` — локальное уведомление (когда приложение живо, а FCM не нужен) |
| `cancelNotification` | `{ actionId?: string, taskId?: string }` | `{}` — убрать уведомление, когда решение принято в самом приложении |

События (`addListener`): `pushToken` `{ token }` — FCM-токен обновился; `permissionsChanged`
`{ permissions }` — вернулись из настроек; `notificationOpened` `{ type, taskId, chatId, actionId }` —
тап по уведомлению (в т.ч. холодный старт: данные отдаются при `addListener`/через `getLaunchNotification`
→ `{ type, taskId, chatId, actionId } | {}`).

Нативные уведомления: канал `conexy_tasks` (importance HIGH) для confirm/phone_needed, `conexy_done`
(DEFAULT) для completed. Кнопки «Разрешить»/«Отклонить» — `BroadcastReceiver`, который делает
`POST {baseUrl}/api/mobile/actions/{actionId}` с `Authorization: Bearer <token>` из `setAuth`
и затем убирает уведомление. `POST_NOTIFICATIONS` запрашивается как обычное разрешение `notifications`.

## Фронтенд (`frontend/src`) — как реализовано и отличия

- `services/phoneAgent.ts` — мост плагина; `utils/phoneTool.ts` — чистая логика (разбор `PhoneTool`,
  дедупликация `callId`, таймауты). `signalrService` получил `onPhoneTool`/`onConnected`/`invokeIfConnected` —
  второго соединения нет.
- Плагин берётся через `getNativePlugin('PhoneAgent')` (`utils/mobile.ts`: `isPluginAvailable` + `registerPlugin`,
  fallback на `Capacitor.Plugins`). `startPhoneAgent()` вызывается из `main.tsx`.
- Настройка «Агент телефона» — `localStorage['conexy.phoneAgent.enabled']` (по умолчанию включена). Если выключена,
  `usePhone` не шлётся, а пришедший `PhoneTool` получает `isError=true`. Флаг онбординга — `conexy.phoneAgent.onboarded`.
- Таймаут инструмента: 120 с, для `phone_request_permission` и `phone_gallery_pick` — 300 с. Результат шлётся через
  `invokeHub` с 5 повторами (пока хаб переподключается); `output` не усекается.
- `RegisterPhone` шлётся после каждого подключения хаба и по `permissionsChanged`; сразу за ним — `SetPhoneForeground`.
- При выходе: `DELETE /api/mobile/push-token` с явным Bearer (до отзыва JWT), затем `setAuth({token:null})`.
- Тап по уведомлению открывает чат по `chatId` (если чата нет в списке — `GET chat` и добавление).
- Заголовки карточек подтверждения и подписи в ленте берутся из `phoneTool.<имя>` в локалях.

**Требование к серверу:** `PhoneToolResult` с `IMAGE_BASE64` легко превышает 32 КБ (дефолт
`MaximumReceiveMessageSize` у SignalR) — нужно поднять лимит хаба, иначе соединение будет закрываться.

### Уточнения реализации плагина (Android)

- `notificationOpened` / `getLaunchNotification` дополнительно содержат `dangerous: boolean`. При холодном
  старте данные приходят **и** retained-событием при `addListener`, **и** через `getLaunchNotification`
  (одни и те же) — клиенту нужно дедуплицировать по `(type, taskId, chatId, actionId)`.
- Для `confirm` с `dangerous=true` кнопка «Разрешить» в уведомлении **открывает приложение** (тихого POST нет),
  «Отклонить» работает без открытия. Если `POST` не удался (нет токена/сети), уведомление остаётся, показывается toast.
- `phone_gallery_pick` копирует выбранное в `files/picked` и возвращает абсолютные пути (не `content://`);
  эти пути принимают `phone_image_view`, `phone_widget_photo`, `phone_set_wallpaper`, `phone_share`, `phone_file_copy`.
- `phone_clear_cache` (чужие приложения), `phone_force_stop_app`: без службы доступности только открывают экран
  «Сведения о приложении» и честно сообщают, что действие не выполнено; со службой — кликают и проверяют результат
  (размер кэша через `usage_access`, флаг «остановлено»). `phone_uninstall_app` ждёт до 40 с подтверждения пользователя.
- Файловые инструменты: корень — `/storage/emulated/0` (при `all_files`), иначе `Android/data/conexy.app/files/workspace`;
  `Android/data` и `Android/obb` других приложений, а также корневые каталоги (`DCIM`, `Download`, …) от удаления/перемещения защищены.
- `phone_swipe direction`: направление движения пальца (`up` — палец вверх, лента прокручивается вниз).
- `requestAll` пропускает окончательно отказанные runtime-разрешения (`denied`), запрашивает только `prompt`.

## Серверная реализация: уточнения к контракту

Что именно делает сервер (`ConexyAI/Service/Phone/*`, `Service/Push/*`, `Controller/MobileController.cs`):

- **Мост**: `PhoneToolBridge`. Событие `PhoneTool` — `{ callId: string, taskId: string, tool, arguments }`; `callId`
  и `taskId` — строки-GUID. Тайм-аут вызова — 5 мин, для `phone_gallery_pick` и `phone_request_permission` — 10 мин.
  Результат принимается только от того подключения, которому ушёл вызов. Аргументы перед отправкой проверяются по схеме:
  телефон получает только известные поля (`arguments` — нормализованный JSON). Если телефон отключился —
  ошибка «Связь с телефоном потеряна…», агент больше не вызывает `phone_*` в этом ходе.
- **Подтверждения (C/D)** — не новое событие, а та же карточка, что у команд ПК: `OnPendingActionCreated`
  (`command` = «Удалить приложение com.foo», `workingDirectory` = «Телефон: <модель>», `description` = пояснение,
  `isDangerous` = true для D). `actionId` равен `requestId`. Решение — хаб `ConfirmAction` (веб) или
  `POST /api/mobile/actions/{actionId}` (уведомление). Таймаут — 10 мин, считается отказом. Режима «разрешить всё»
  для телефона нет.
- **`POST /api/mobile/actions/{actionId}`**: `200 {ok, approved}` — принято; `409` — решение уже принято или время
  вышло; `404` — действия нет **или оно чужое**; `400` — не GUID / нет тела. Кнопке уведомления `409`/`404` —
  не ошибка: просто убрать уведомление.
- **`ask_user`** без ПК обслуживает сервер: событие `OnDeviceQuestion` `{requestId, taskId, question, options}` в
  группу задачи, ответ — существующий хаб-метод `AnswerDeviceQuestion` (как для ПК).
- **`POST/DELETE /api/mobile/push-token`**: токен 20–1024 символов без пробелов; максимум 10 токенов на пользователя
  (при превышении вытесняется самый давний по `LastSeenAt`); повтор того же токена обновляет `LastSeenAt`; токен,
  пришедший от другого аккаунта, переходит к нему. `DELETE` чужого токена ничего не делает (204).
- **Push**: `confirm` — когда приложение не на экране (`SetPhoneForeground(false)` или телефон офлайн);
  `completed` — после **любой** завершённой задачи пользователя, если приложение не на экране и push настроен
  (для incognito `body` нейтральный, `chatId` пустой, `title` = «Задача готова»; иначе `title` = название чата, `body` —
  начало ответа до 140 символов); `phone_needed` — если инструмент телефона нужен, а телефона нет: сервер ждёт
  подключения 60 с, шлёт push и ждёт ещё 4 мин. FCM: `android.priority=HIGH`, `ttl=600s`, только `data` (все значения — строки;
  `dangerous` = `"true"`/`"false"`). Токены, на которые FCM ответил `UNREGISTERED`/404, удаляются.
- **`usePhone`**: сервер игнорирует флаг (работает как обычно), если телефон не подключён (ждёт регистрации до 3 с),
  либо это чат «Ученики». Инструменты `phone_*` модели выдаются только при `usePhone` **и** живом подключении телефона
  этого пользователя. Чат (flash/pro) с `usePhone` идёт через агентский цикл, профиль `phone` (phone_*, web_search,
  fetch_web_page, search_user_chats, ask_user). Токены такого хода списываются из агентского пула тарифа,
  запрос — как запрос чата.

### Отклонения / заметки

- Хаб-метод `PhoneToolResult` принимает `callId` строкой (GUID); неверный GUID → `false`.
- Значения `direction` для `phone_swipe`: `up|down|left|right`.
- Уведомление `confirm` убирается push-ом `dismiss` (см. выше). Если он не дошёл (нет сети), кнопка в старом
  уведомлении получит 409 `already_resolved` — клиент просто закрывает уведомление.
