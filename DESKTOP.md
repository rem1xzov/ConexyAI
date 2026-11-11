# ConexyAI Desktop: CI, хуки, прокси, релизы

Документ для тех, кто ставит клиент в команду или собирает релиз.

## 1. Headless / CI

Клиент умеет работать без TUI — один ход агента и результат в stdout:

```bash
conexy -p "исправь падающие тесты" --dir . --output-format json
conexy -p "проверь код" --permission-mode plan --max-turns 20
git diff main | conexy -p "review these changes" --output-format text
```

| Флаг | Значение |
|---|---|
| `-p`, `--print` | неинтерактивный режим (обязателен) |
| `--output-format text\|json\|stream-json` | `text` — только ответ; `json` — один объект; `stream-json` — JSONL-события |
| `--permission-mode default\|auto\|plan` | по умолчанию команды **не** подтверждаются; `auto` разрешает их |
| `--auto` | то же, что `permission-mode auto` |
| `--plan` | только чтение и план |
| `--max-turns N` | лимит вызовов инструментов за ход (клиентский) |
| `--effort low\|high` | «усилие» модели; сохраняется и для TUI. Агент на flash сервер может не форвардить reasoning — флаг влияет там, где модель его поддерживает |
| `--agent coder\|cowork` | профиль агента |
| `--team` | включить оркестр (если тариф позволяет) |
| `--with-mcp` | поднять локальные MCP-серверы (дольше, нужен Node/uv) |
| `--no-hooks` | не выполнять `.conexy/hooks.json` |
| `--timeout N` | общий таймаут в секундах |
| `--chat <guid>` | продолжить конкретный чат |
| `--json` | алиас `--output-format json` |

Коды выхода: `0` успех, `1` ошибка, `2` нет входа/ключ отклонён, `3` лимит тарифа,
`4` предыдущий ход ещё идёт, `64` неверные аргументы, `65/66` проблема с рабочей папкой,
`130` отменено.

Пример GitHub Actions:

```yaml
- name: Conexy review
  env:
    CONEXY_KEY: ${{ secrets.CONEXY_KEY }}
    CONEXY_URL: https://conexyai.ru
  run: |
    git diff origin/main...HEAD | conexy -p "ревью изменений: баги, риски, тесты" \
      --dir . --permission-mode plan --output-format text
```

> В CI не включайте `--auto` без песочницы: агент сможет выполнять команды в runner'е.

## 2. Хуки проекта

Файл `.conexy/hooks.json` (формат по смыслу как у Claude Code):

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "file_write|apply_patch",
        "hooks": [
          { "type": "command", "command": ".conexy/guard.ps1", "shell": "powershell", "timeout": 30 }
        ]
      }
    ],
    "PostToolUse": [
      {
        "matcher": "file_write|apply_patch",
        "hooks": [
          { "type": "command", "command": "npx prettier --write ." }
        ]
      }
    ],
    "PostToolUseFailure": []
  }
}
```

- `matcher`: `*`, список через `|`/`,` или регулярное выражение.
- Обработчик получает JSON на stdin: `hook_event_name`, `tool_name`, `tool_input`, `cwd`, `project_dir`.
- Блокировка: `exit 2` (stderr — причина) или JSON `{"hookSpecificOutput":{"permissionDecision":"deny","permissionDecisionReason":"..."}}`.
- Замечание модели: `additionalContext` или `systemMessage` в JSON; оно добавляется к результату инструмента.
- `timeout` по умолчанию 60 с, максимум 600. Сломанный/зависший хук не ломает задачу.
- На Linux/macOS скрипт хука должен быть исполняемым (`chmod +x .conexy/guard.sh`).
- Плейсхолдеры: `${CONEXY_PROJECT_DIR}`, `${CONEXY_ROOT}`, `$CONEXY_PROJECT_DIR`.

**Доверие.** Хуки — это код из репозитория. Они выполняются только после явного доверия папке:

```bash
conexy hooks status     # есть ли файл и доверие
conexy hooks trust      # доверять текущей папке (запоминается хеш файла)
conexy hooks forget     # снять доверие
```

Если `hooks.json` изменился, хеш не совпадёт и хуки снова не выполнятся. В демоне и headless
недоверенные хуки пропускаются; `CONEXY_TRUST_HOOKS=1` — явное доверие для CI.

## 3. Прокси

Для корпоративных сетей:

```bash
conexy proxy set http://user:pass@proxy:3128
conexy proxy off
conexy proxy            # показать текущий
```

Приоритет: `CONEXY_HTTP_PROXY` → `HTTPS_PROXY`/`HTTP_PROXY` → сохранённый в конфиге.
Поддерживаются `http://`, `https://`, `socks4://`, `socks5://`. Локальные адреса идут напрямую.
Прокси применяется к HTTP, SignalR/WebSocket и скачиванию обновлений.

## 4. Диагностика

```bash
conexy doctor
conexy doctor --json
```

Проверяет: версию/ОС, папку настроек, рабочую папку, прокси, хуки, PowerShell 7, git, node,
winget, демон, вход, тариф, обновление и SignalR. Ничего не меняет.

## 5. Релиз: подпись, установщик, зеркало

### GitHub Actions (без сервера и SSH)

В репозитории есть `.github/workflows/release.yml`. Достаточно поставить тег:

```bash
git tag v0.9.29
git push origin v0.9.29
```

Сборка сама соберёт все RID, подпишет (если заданы secrets), соберёт установщик, приложит файлы к GitHub-релизу и загрузит их на зеркало.

Secrets в GitHub → Settings → Secrets and variables → Actions:

| Secret | Что это |
|---|---|
| `WINDOWS_CERT_PFX_BASE64` | `[Convert]::ToBase64String([IO.File]::ReadAllBytes('conexy.pfx'))` |
| `WINDOWS_CERT_PASSWORD` | пароль PFX |
| `UPDATE_SIGNING_KEY_BASE64` | base64 от `.update-signing-key.pem` |
| `DEPLOY_HOST` | хост зеркала (без него шаг загрузки пропускается) |
| `DEPLOY_USER` | SSH-пользователь |
| `DEPLOY_PATH` | папка на сервере, например `/var/www/conexyai/downloads/` |
| `DEPLOY_SSH_KEY` | приватный deploy-ключ SSH (не пароль) |

Без `WINDOWS_CERT_PFX_BASE64` exe будут неподписанными; без `UPDATE_SIGNING_KEY_BASE64` не будет `latest.json`.

### Ключ подписи манифеста

Один раз на машине релиза:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out .update-signing-key.pem
```

Публичный ключ уже встроен в клиент (`UpdateSigning.PublicKeyPem`). Приватный ключ
`.update-signing-key.pem` **не коммитится** и хранится в бэкапе: без него новые версии не пройдут
проверку у установленных клиентов. Ротация ключа = новый клиент с новым публичным ключом.

### Сборка

Можно без аргументов — всё берётся из env:

```bash
export CONEXY_VERSION=0.9.29
export CONEXY_PFX_PATH=/secure/conexy.pfx
export CONEXY_PFX_PASSWORD='пароль-от-pfx'
export CONEXY_TIMESTAMP_URL=http://timestamp.<ваш-УЦ>
export CONEXY_SIGNING_KEY=/secure/.update-signing-key.pem
export CONEXY_URL_BASE=https://conexyai.ru/downloads
pwsh scripts/publish.ps1
```

Или аргументами:

```powershell
pwsh scripts/publish.ps1 -Version 0.9.29 `
  -PfxPath C:\keys\conexy.pfx -PfxPassword (Read-Host -AsSecureString) `
  -TimestampUrl http://timestamp.<ваш-УЦ>
```

Переменные: `CONEXY_VERSION`, `CONEXY_RIDS` (через запятую), `CONEXY_OUTPUT`, `CONEXY_URL_BASE`,
`CONEXY_SIGNING_KEY`, `CONEXY_PFX_PATH`, `CONEXY_PFX_PASSWORD`, `CONEXY_CERT_THUMBPRINT`,
`CONEXY_TIMESTAMP_URL`, `CONEXY_SKIP_MANIFEST=1`. Аргументы важнее env.

Скрипт:

1. `dotnet publish` под `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64` (RID настраиваются `-Rids`).
2. Подписывает Windows-exe через `signtool` (если задан PFX или thumbprint).
3. Собирает установщик Inno Setup (`dist/conexy-setup-<версия>.exe`) и подписывает его.
4. Генерирует `dist/latest.json` и подписывает ECDSA P-256, затем проверяет подпись обратно.

Без сертификата скрипт предупреждает: SmartScreen и антивирусы будут ругаться. Для РФ
используйте сертификат российского УЦ (подпись кода) — это снимает «Не удалось проверить
издателя» и ложные срабатывания.

### Публикация

1. Загрузите **все** файлы из `dist/` на `https://conexyai.ru/downloads/` (имена не менять).
2. `latest.json` — **последним**: клиент не должен увидеть манифест раньше файлов.
3. Те же файлы приложите к GitHub-релизу `v<версия>` (фолбэк для старых клиентов).
4. Проверка: `conexy updateverify dist` → `PASS`.

Клиент проверяет обновление так:

1. `GET {сервер}/downloads/latest.json` (можно переопределить `CONEXY_UPDATE_MANIFEST`).
2. Подпись манифеста встроенным ключом; версия должна быть новее; файл — под текущий RID.
3. SHA-256 из подписанного манифеста — единственный источник для зеркала.
4. Если зеркала нет — GitHub-релиз + digest из GitHub API (как раньше).

## 6. Установщик

`installer/conexy.iss` (Inno Setup 6):

- ставит в `%LOCALAPPDATA%\ConexyAI` без UAC;
- по галочке добавляет `conexy` в `PATH` (HKCU) и ярлык на рабочем столе;
- удаление убирает запись из `PATH`;
- после установки предлагает открыть `conexy` в терминале (там же вводится ключ).

## 7. VS Code

`vscode-extension/` — тонкая обёртка: терминал, «Спросить агента», «Объяснить выделенное»,
«Проверить изменения». Сборка:

```bash
cd vscode-extension
npm install
npm run compile
npx @vscode/vsce package
code --install-extension conexy-vscode-0.1.0.vsix
```

## 8. Платформы

- Ядро (файлы, поиск, bash, MCP, GitHub-токен) работает на Windows, Linux и macOS.
  `scripts/publish.ps1` собирает `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.
- Только Windows: плавающий круг, автозапуск демона, управление окнами/мышью/клавиатурой,
  `screen_capture`, `install_software` (winget), вставка из буфера. На других ОС эти инструменты
  возвращают понятный отказ, а не падают.
- Автозапуск демона на Linux/macOS пока не реализован: запускайте `conexy daemon` вручную или
  через systemd/launchd.
- Headless и хуки работают одинаково на всех ОС; хуки без `shell` выполняются через `cmd` (Windows)
  или `/bin/sh` (Unix), с `shell: powershell` — через PowerShell/pwsh.

---

## 9. Релизный чек-лист

1. `dotnet build` клиента и сервера без ошибок; `conexy selftest` — все PASS.
2. `dotnet run --project ConexyAI.Tests` — ALL TESTS PASSED.
3. `pwsh scripts/publish.ps1 -Version <x.y.z>` — exe подписаны, установщик собран, `latest.json` PASS.
4. `conexy -p "ответь одним словом: ок" --output-format json` — код 0 и валидный JSON.
5. Загрузить файлы, `latest.json` — последним; проверить `conexy /update` на чистой машине.
6. Прогнать `QA_REGRESSION.md`, включая сценарий write-only и хуки.
