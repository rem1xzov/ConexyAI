# DeepSeek Harness как референс для ConexyAI

**Источник:** локальный чекаут `C:\Users\bkrav\OneDrive\Рабочий стол\deepseek-harness`
(remote `deepseek-ai/deepseek-harness`, HEAD `fcc0ad46`, ветка релиза dsh 0.2.2-rc.1).
Второй чекаут `C:\Users\bkrav\deepseek-harness` старше (`0d1f500`) — использовался только для сверки.
**Дата обзора:** 2026-11-20. **Метод:** read-only разбор документации и кода пятью параллельными
исследованиями (ядро/промпты, инструменты/безопасность, контекст/скиллы, команда/goal/workflow,
docs/UX/протоколы) + сверка с текущим кодом ConexyAI. Ничего в DSH не менялось.

> Этот файл — карта идей, а не задача «скопировать». Правила нашего кода — в [CONEXY.md](../CONEXY.md).

---

## 1. TL;DR — что стоит взять в первую очередь

1. **Process-минимум за день:** Agent Notes (`docs/notes/{proposed,implemented,rejected}` с фиксированным
   шаблоном «Problem / Decision / Alternatives / Consequences») + бюджет слов для standing-доков.
   Дёшево, сразу улучшает работу и агентов, и людей.
2. **Advisory repeat-reminder отдельно от жёсткого отказа.** У DSH повтор инструмента с каноничными
   аргументами даёт подсказку с эскалацией `[3,5,8]`, и только потом — жёсткий стоп. Наш
   `AgentLoopGuard` сразу отказывает; мягкая ступень снижает «тупые» отказы на нормальной работе.
3. **Turn-exit evidence guard расширить:** добавить в `AgentCompletionGuard` открытые todo и
   «одно и то же наблюдение» как отдельные классы улик + жёсткий кап продлений (у DSH
   `maxContinuations = 3`).
4. **Read-before-write с версией файла.** DSH сравнивает `(hash, mtime)` при записи и возвращает
   `FS_STALE_VERSION` с требованием перечитать. Наш `EditAfterReadGuard` помнит только «читал ли»,
   поэтому внешняя правка между чтением и записью молча затирается.
5. **Поиск с метаданными полноты.** `glob`/`grep` должны возвращать `total`/`truncated` и путь к
   полному списку (spill), а `glob` — сэмплировать по верхним каталогам, чтобы кап не прятал целый
   модуль. Сейчас агент не знает, что список обрезан.
6. **Инструменты как контракт:** CI-проверка, что у каждого инструмента и каждого параметра есть
   осмысленное описание; сгенерированный `docs/tool-catalog.md` (аналог DSH-гейта).
7. **Долгие команды не убивать, а повышать до background-job** с `job_output`/`job_kill`
   (`promoteOnTimeout`). Это убирает целый класс «bash обрезали по таймауту».

Средний горизонт (наиболее ценное): **tool-pipeline (pre/post/deny-guard), typed turn outcome,
compaction по измеренному давлению контекста, активационная модель субагентов (продолжаемые дети,
имена, контроль), Goal + rounds, workflow-фан-аут**. Крупный горизонт: event-sourced session log,
внешние субагенты (Codex/Claude Code/ACP), реестр prompt-секций.

---

## 2. Что такое DSH (кратко, с путями)

- **Всё — плагин** на Cordis: сервис = интерфейс + `ctx`-ключ; расширение = монтирование плагина,
  регистрации откатываются при выгрузке. Прогон = **profile** (упорядоченные bundle-слои + патчи).
  `docs/architecture.md`, `packages/bundle/*`.
- **Turn/step-машина:** `turn/start → (step/start → prompt assemble → stream → tool pipeline → step/end)*
  → turn/end`, typed `TurnEndReasonMap` (`completed|aborted|blocked|error|max-tokens|interrupted|forked`).
  `packages/core/agent-loop/src/index.ts`, `packages/core/session/src/types.ts`.
- **Append-only session log:** `SessionEvent` — источник правды, история модели выводится
  (`deriveMessages`); durable vs live события. `packages/core/session`.
- **Tool pipeline:** `tools/pre-execute` → монотонный deny-only `ToolGuard` → execute (+timeouts) →
  `tools/post-execute` → result; у инструмента есть output-схема и презентация.
  `packages/core/tools/src/pipeline.ts`, `schema.ts`.
- **Промпт — реестр секций**, не склейка: `systemPrompt.section(order)`, инструменты приносят свой
  текст рядом со схемой; стабильный системный узел + отдельный runtime-context после истории.
  `packages/core/system-prompt`, `packages/context/agent-instructions` (вложенная цепочка `AGENTS.md`).
- **Контекст/компакция:** измерение токенов по всему конверту (`token-meter`), бюджет
  `threshold 0.8×window`, хранимый хвост ~16%, сначала детерминированная обрезка tool-результатов
  (head/tail), потом суммаризация; суммаризация — настоящий префикс запроса (переиспользует кэш), ответ —
  чекпоинт из 8 фиксированных секций. `packages/compaction/*`, `packages/spill/*`.
- **Скиллы:** `<name>/SKILL.md` с YAML-фронтматтером; в промпт идут только имя+описание, тело
  подгружается инструментом; каталог перепубликуется по digest. `packages/skill/*`.
- **Субагенты:** seam `ctx.subagents` с провайдерами (in-process spawn, fork из истории, ACP, Codex,
  Claude Code, SDK); имена `lead/agent-N`, `maxDepth=1`, пул 8 активаций, typed `SubagentError`,
  результат `{output, structured, diagnostic, stopReason}` + settlement-уведомление родителю.
  `packages/subagent/*`. **Команда** = shared task board (CAS-задачи, DAG blockedBy, advisory write
  scopes), не чат. `packages/team/agent-team`.
- **Workflow:** модель пишет JS-скрипт, движок исполняет в sandboxed Node (`agent/pipeline/parallel/
  phase/log`, капы `maxTotalAgents`), события `workflow/*`. `packages/workflow/*`.
- **Goal:** event-sourced цель той же сессии (`active/paused/blocked/complete`, `maxGoalRounds`,
  armed/disarmed), round-driver запускает следующий раунд только когда агент простаивает и дети
  закончили. `packages/goal/*`. **Plan:** plan-mode + `exit_plan_mode` (approve/keep planning).
- **Guard ≠ безопасность:** `guard/*` — гигиена цикла (completion-guard, repeat-tool-reminder,
  timeout-policy); разрешения — sandbox-policy, approvals (`ask|never`, fail-closed), permission-presets,
  experimental auto-review. `docs/defensive-patterns/`, `docs/subsystems/approval.md`.
- **Computer/browser use:** реестр провайдеров; DSH явно не координирует экраны между сессиями и не
  даёт click-loop guard — это зона провайдера. Наши lease+курсоры здесь **впереди**.
- **Docs/процесс:** `docs/AGENTS.md` (tier-таксономия «одно место на факт», slop-checklist, бюджеты
  слов), фиксированный формат Agent Note с гейтом, postmortem-инциденты, generated-каталоги
  (tool/config/cli/capability-seams) с CI-гейтами, i18n en/zh с hash-sidecar. `.agents/notes`,
  `docs/postmortem`, `scripts/verify-*`.

---

## 3. Сравнение по подсистемам

Обозначения: ✅ есть и сопоставимо · 🟡 есть частично · ❌ нет · ⭐ у нас лучше.

| Подсистема | DSH | ConexyAI сейчас | Вердикт |
|---|---|---|---|
| Ядро цикла | turn/step-машина с типизированными причинами завершения | `RunLoopAsync`, `string`-результат | 🟡 typed outcome — P1 |
| Pipeline инструментов | pre/deny/execute/post/result, output-схемы | один switch + отказные гейты впереди | 🟡 middleware — P1 |
| Промпт | реестр упорядоченных секций, инструмент приносит свой текст | большой устав + `PromptFragments` + `Guidance` | 🟡 реестр секций — P2 |
| Сообщения/сессия | append-only лог, derived history, replay/resume | `List<ChatMessage>` на прогон + история в БД | 🟡 event-log — P2 (только если нужен resume) |
| Маршрутизация модели | seam `ctx.llm`, durable model selection, `prepareCall` | `ConexyModelType` + `LlmRoute` + локальный транспорт | ✅ достаточно; не добавлять fallback-цепочки |
| Компакция | давление по токенам, prune→summarize, 8-секционный чекпоинт | `AgentContextCompressor` по эвристикам | 🟡 бюджет по давлению — P1 |
| Spill | post-execute политика с токен-бюджетом, head/tail | `ToolResultSpill` (50КБ) вызывается точечно | 🟡 единая политика — P1 |
| Скиллы | SKILL.md, progressive disclosure, digest-каталог | `.agents/skills` + `read_skill` + сводка | ✅ близко; добавить flags/линт (P2) |
| Долгая память | честно отсутствует; session-query по корпусу | `UserMemoryFact` + cross-chat digest + `search_user_chats` | ⭐ у нас есть память — не копировать session-query |
| Deliverables | `present` + workspace-changes | `create_document`, артефакты, diff | ✅ хватает |
| Субагенты | активации, продолжаемые дети, fork, внешние провайдеры | `spawn_agents` one-shot + scope + бюджеты | 🟡 activation/continuation — P1 |
| Команда | CAS task board + DAG + write scopes | статические доски `team_message` | 🟡 task board — P2 |
| Workflow | JS-скрипты в sandbox, капы | нет (только `spawn_agents`) | ❌ plan-DSL — P2 |
| Goal/rounds | есть, event-sourced | нет | ❌ — P1/P2, высокая продуктовая ценность |
| Guard завершения | failed action + open todos + repeated output + continuation cap | failed command + repeated output | 🟡 дополнить — P0 |
| Повторные вызовы | мягкая подсказка [3,5,8] + канон аргументов | жёсткий отказ `AgentLoopGuard` | 🟡 двухступенчато — P0 |
| Sandbox/разрешения | per-call режим + escalation ladder + fail-closed approvals | docker-песочница, approval-карточки, классификатор команд | ⭐ классификатор опасных команд — наше преимущество |
| Read-before-write | версия файла, `FS_STALE_VERSION` | «читал ли» без версии | 🟡 version CAS — P0 |
| Поиск | `total/truncated` + spill, сэмплирование по каталогам | нет метаданных полноты | 🟡 — P0 |
| Тесты/фидбек | LSP без диагностики, тестов нет; guard как фидбек | `run_tests`, `get_diagnostics`, LSP, аудитор кода | ⭐ у нас сильнее |
| MCP | namespacing + hash при потере, credential-шаблоны с редакцией | `mcp__*`, OAuth, санитайзер | 🟡 hardening — P1 |
| Долгие команды | promote в background job + job tools | таймаут, процесс убивается | 🟡 — P0/P1 |
| Клиентские протоколы | durable follow-стримы + transient chunks, pendingInteraction, shortcuts | SignalR события, `OnAgentStatus`/`OnScreenshot` | 🟡 pendingInteraction/trajectory — P2 |
| Процесс/доки | Agent Notes + tiers + бюджеты + generated catalogs | `DESKTOP.md`, `QA_REGRESSION.md`, CONEXY.md | ❌ Agent Notes — P0 (дёшево) |
| Computer use на экране | провайдер-специфично, без координации | lease ввода, цвета, оверлей, frame-guard | ⭐⭐ у нас впереди — только усилить |
| Исполнение на ПК/телефоне | нет (только свои процессы) | `DeviceToolBridge`, phone-агент | ⭐⭐ наше преимущество |
| Локальная модель | нет (только провайдеры) | `AiUpstream:Local` + device-bridge | ⭐ наше преимущество |
| Карта репозитория | нет; вместо неё вложенные AGENTS.md | `RepoMapService` + `repo_map` + клиентская карта | ⭐ наше преимущество |

---

## 4. План внедрения

### P0 — дешёво и сразу полезно (дни)

1. **Agent Notes** — `docs/notes/{proposed,implemented,rejected}/YYYY-MM-DD-slug.md`, шапка
   `# Agent Note: <title>` / `Status: …`, обязательные разделы Problem / Decision / Alternatives /
   Consequences (+ Acceptance для proposed). В `CONEXY.md` добавить правило «решение с альтернативами
   фиксируется заметкой в том же PR». По желанию — консольный `DocsLint` в тестах.
   Референс: `.agents/notes/README.md`, `docs/AGENTS.md`.
2. **Двухступенчатый repeat-reminder**: `RepeatToolReminder` — канонизация аргументов (глубокий sort),
   пороги попыток `[3,5,8]`: мягкая подсказка → подробная → только потом жёсткий отказ
   `AgentLoopGuard`. Сбрасывается на новое сообщение пользователя.
   Референс: `packages/guard/repeat-tool-reminder`.
3. **Completion guard: + open todos + finding signature + continuation cap.** Новые
   `CompletionEvidenceKind.OpenTodo` и `Signature` (эскалация «одно и то же наблюдение» один раз,
   потом отпустить), жёсткий лимит продлений (как `maxContinuations=3`).
   Референс: `packages/guard/completion-guard/src/evidence.ts`.
4. **Version-CAS при записи**: при `file_read` запоминать `(sha256, mtime)`; в write/edit сравнивать и
   возвращать `[stale] файл изменился с момента чтения — перечитай` (аналог `FS_STALE_VERSION`).
   Файлы: `ConexyAI/Service/ConexyWorkspaceService.cs`, `EditAfterReadGuard`, `StrReplaceEditor`-пути.
   Референс: `packages/fs/fs-observation-policy`.
5. **Полнота поиска**: `grep`/`glob` возвращают `total`, `truncated`, путь к полному списку (spill);
   `glob` сэмплирует по верхним каталогам. Референс: `packages/fs/tool-fs-search`.
6. **Контракт каталога инструментов**: тест, что у каждого инструмента и параметра есть описание
   (уже есть зачатки в `AgentToolCatalogTests`), + сгенерированный `docs/tool-catalog.md` из
   `AgentToolCatalog` с проверкой в тестах. Референс: `docs/tool-catalog.md`, `verify-tool-catalog`.
7. **Prompt/команды для длинных процессов**: `promoteOnTimeout` в `BashTool` → `JobRegistry` с
   `job_output`/`job_kill` (в десктопе уже есть `bg_*` — сделать единый механизм и на сервере).
   Референс: `packages/shell/tool-bash`, `packages/jobs`.

### P1 — месяц, самый большой выигрыш (1–3 недели суммарно)

8. **Tool-pipeline seam**: разбить исполнение инструмента на этапы
   `pre-execute → deny-guard (только отказ) → execute → post-execute`; гейты (`plan/team/read/loop/
   computer/scope/timeout/spill`) становятся слушателями, а не кодом в цикле. Затем — **per-step
   доступность инструментов** (`when`), вместо статического профиля.
9. **Typed turn outcome + request header**: `Task<TurnOutcome>` с `Reason`, текстом и уликами;
   логировать канонический заголовок запроса, чтобы проверять стабильность префикса в тестах
   (сейчас это знание живёт в комментариях).
10. **Компакция по давлению**: бюджет `min(0.8×window, window−reserve−headroom)`, хвост ~16%,
    сначала детерминированная обрезка (head/middle/tail), суммаризация — только если не помогло;
    чекпоинт из фиксированных секций с сохранением путей/команд. Spill — одна post-execute политика
    с токен-бюджетом.
11. **Активационная модель субагентов**: `StartActivation(spec) → {ChildId, Name, Result, Dispose}`,
    имена `lead/agent-N`, пул активаций, typed ошибки; продолжаемые дети
    (`send_message`/`wait_agent`/`interrupt_agent`/`list_agents`) вместо one-shot + статического
    `agents_status`; settlement-уведомление родителю; `SubagentPreset {Tools, Persona, ModelRoute}`
    (локальная модель — один из пресетов); pinned approval/sandbox на ребёнка. Наши бюджеты и
    input-lease/курсоры сохранить — в DSH их нет.
12. **Goal + rounds**: новая сущность цели на чат/задачу (`active|paused|blocked|complete`,
    `MaxGoalRounds`, CAS), раунд-драйвер продолжает только когда агент простаивает и дети закончили;
    UI-индикатор и ручное complete/blocked. Это то, что в этой сессии уже показало ценность.

### P2 — квартал

13. **Workflow-фан-аут**: инструмент `workflow` с JSON/C# plan-DSL (`agent`, `pipeline`, `parallel`,
    `phase`, `log`), капы (`maxTotalAgents/maxConcurrent`), фоновые запуски; вместо JS-VM — наш
    C#-исполнитель в песочнице. Референс: `packages/workflow`, `docs/subsystems/workflow.md`.
14. **Team task board**: задачи с `Revision/Status/Owner/BlockedBy/WriteScopes`, CAS-апдейты, проверка
    циклов, сериализация per-board (заменяет статические `TeamBoards`). Референс:
    `packages/team/agent-team/src/task-graph.ts`.
15. **Event-sourced session log** (только если нужны replay/resume/аудит): `AgentEvent` + derive history
    + projection-таблицы; сейчас у нас `ChatMessage` + `ChatAgentEvent` частично это покрывают.
16. **Внешние субагенты**: провайдер, запускающий Codex/Claude Code/ACP-CLI (в т.ч. на ПК
    пользователя через device-канал) и возвращающий результат в нашу активацию. Огромная ценность для
    «агентской платформы», но это отдельный проект.
17. **Prompt-секции**: `IPromptSection {Order, Title, Render}` + каждый tool-модуль приносит свой
    текст; устав собирается из секций, а не строки. Улучшает кэш и облегчает A/B промптов.
18. **Капабилити-документация**: таблица seam'ов (интерфейс → провайдеры → потребители) и гейт её
    полноты; полезно, когда появится второй провайдер модели/песочницы/браузера.
19. **Клиентские протоколы**: контракт `pendingInteraction` (approval/plan/answer) с одной типизацией
    на web/desktop; «trajectory»-вид рядом с чатом; реестр команд/хоткеев с дефолтами по устройству.
20. **Док-процесс дальше**: бюджеты слов standing-доков (CONEXY.md/DESKTOP.md/QA_REGRESSION.md),
    postmortem-страницы на инциденты, generated-каталоги (события SignalR, команды TUI, схемы
    инструментов) с CI-проверкой.

---

## 5. Что НЕ переносить

- **Cordis как фреймворк** (плагины/`ctx`/fibers/effects/HMR/профили-патчи): на C# DI + явные
  интерфейсы достаточно; портировать «плагинность» без нужды — месяцы ритуала.
- **~60 пакетов на одну фичу** и generated-каталоги с i18n-парами в масштабе DSH — церемония больше
  продукта; нам хватает CONEXY.md + небольшого DocsLint.
- **JSONL-поколения сессий, миграции форматов 0–4, cross-process lease, проекционные кэши**, если не
  делаем resume/replay: EF Core + append-only таблица проще.
- **Event-sourced проекции везде** (доски, goal, каталоги): начинать с одного агрегата, не с системы.
- **PTC/JS-`vm` workflow runtime**: нам лучше JSON/C# DSL с теми же хуками.
- **Dual-language doc-gates**, hash-sidecar, 927 архивных заметок, плагин-маркет с provenance — не для
  нашей команды.
- **Per-agent scoped tool registries + `deferLoading`/`toolUpdate`** — сложность без нашего выигрыша.
- **«Plan mode не является границей безопасности»** — не копировать: у нас режим/подтверждения —
  реальный контроль.
- **Мягкие fallback-цепочки моделей и shadow-pricing token-meter** — у нас биллинг уже точный.

---

## 6. Где мы уже впереди (не потерять при рефакторингах)

1. **Computer use со смыслом:** input-lease на один ПК, `AgentCursor` с цветами, оверлей, guard по
   перцептивному кадру. У DSH компьютерный use — только регистрация провайдера, координации нет.
2. **Исполнение на пользовательском ПК и телефоне** (`DeviceToolBridge`, phone-агент) — у DSH
   инструменты живут в его собственных процессах.
3. **Локальная модель как субагент** (сервер или ПК пользователя через device-канал) — у DSH только
   свои провайдеры.
4. **Классификатор опасных команд** + подтверждения + смена режима `/control` — DSH полагается лишь
   на sandbox-политику и approval.
5. **Карта проекта** `repo_map` (серверная и клиентская) — в DSH роль «карты» играет вложенная
   цепочка `AGENTS.md`, но она static и не даёт дерева/символов.
6. **Тестовая петля агента**: `run_tests`, `get_diagnostics`, LSP, code auditor, completion/integrity
   гейты — у DSH этого набора нет.
7. **Память пользователя и cross-chat digest** — у DSH долгой памяти нет вообще.

---

## 7. Быстрый следующий шаг (если продолжаем тему)

Три маленьких PR из P0, которые можно сделать независимо:
`docs/notes` + правило в CONEXY.md → `RepeatToolReminder` → version-CAS в workspace-сервисе.
Дальше — completion guard и search completeness; затем один большой P1 (subagent activation), потому
что он напрямую усиливает уже выпущенную автономную команду.

---

## 8. Что читать в DSH для деталей

| Тема | Путь |
|---|---|
| Архитектура и seams | `docs/architecture.md`, `docs/capability-seams.md` |
| Цикл агента | `docs/agent-lifecycle.md`, `packages/core/agent-loop/src/agent.ts` |
| Инструменты и pipeline | `docs/subsystems/tools.md`, `packages/core/tools/src/pipeline.ts`, `docs/cookbook/adding-a-tool.md` |
| Системный промпт | `docs/subsystems/system-prompt.md`, `packages/core/system-prompt` |
| Контекст/компакция/spill | `docs/subsystems/compaction.md`, `docs/subsystems/spill.md`, `packages/compaction/*` |
| Скиллы | `docs/subsystems/skills.md`, `packages/skill/*` |
| Субагенты/команда/workflow | `docs/subsystems/{subagent,agent-team,workflow}.md`, `packages/{subagent,team,workflow}/*` |
| Goal/plan/preset | `docs/subsystems/{goal,plan,permission-presets}.md`, `packages/{goal,plan,preset}/*` |
| Guard/завершение | `packages/guard/*`, `docs/defensive-patterns/` |
| Разрешения/песочница | `docs/subsystems/{approval,sandbox}.md`, `packages/sandbox/*`, `packages/interaction/*` |
| Компьютер/браузер | `docs/subsystems/{computer-use,browser-use}.md`, `packages/{computer-use,browser-use}/*` |
| Процесс/заметки/доки | `.agents/notes/README.md`, `docs/AGENTS.md`, `scripts/verify-*` |
| Клиентские протоколы | `docs/subsystems/web-client.md`, `docs/subsystems/commands.md`, `packages/client/*` |
