# ConexyAI для VS Code

Тонкая обёртка над клиентом `conexy`: терминал в папке проекта и неинтерактивные запросы `-p`
с выводом в канал «ConexyAI».

## Что умеет

- **Conexy: Открыть агента в терминале** — полноценный TUI в текущем workspace.
- **Conexy: Спросить агента** — вопрос или задача; выделенный код уходит агенту как контекст.
- **Conexy: Объяснить выделенное** — правый клик в редакторе.
- **Conexy: Проверить незакоммиченные изменения** — агент читает `git diff` и даёт отчёт.

Команды используют `conexy -p ... --output-format text`, поэтому работают и в CI, и в обычном
терминале. Подтверждения команд по умолчанию отклоняются: включите `conexy.autoApprove`
только для доверенных проектов.

## Установка

1. Установите клиент: `conexy login --url https://conexyai.ru --key cnx_...`
2. Соберите расширение:

```bash
cd vscode-extension
npm install
npm run compile
npx @vscode/vsce package
code --install-extension conexy-vscode-0.1.0.vsix
```

Для разработки: откройте папку `vscode-extension` в VS Code и нажмите `F5`
(Extension Development Host).

## Настройки

| Настройка | По умолчанию | Что делает |
|---|---|---|
| `conexy.executable` | `conexy` | Путь к клиенту, если его нет в `PATH` |
| `conexy.extraArgs` | `[]` | Доп. аргументы, например `["--effort", "medium"]` |
| `conexy.autoApprove` | `false` | Добавляет `--auto`: команды без подтверждения |

## Ограничения

- Diff показывается в канале вывода, а не как нативные inline-diff'ы (следующий шаг).
- Для Windows клиент должен быть в `PATH` (установщик добавляет его по галочке).
