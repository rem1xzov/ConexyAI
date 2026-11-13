using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ConexyAI.Service;

// LOOP_GUARD: добавлено 2026-09-27
/// <summary>
/// Защита от зацикливания внутри ОДНОГО запуска агента.
/// <para>
/// Держит скользящее окно последних вызовов инструментов и отказывает в повторе ДО того, как команда
/// уйдёт в терминал: если одна и та же команда уже три раза подряд вернула одно и то же, четвёртый
/// запуск ничего нового не даст, а токены и время съест. Отдельно считает обращения к интернету
/// (поиск + чтение страниц) — но только внутри ОДНОГО хода модели: счётчик сбрасывается на каждом
/// шаге цикла, поэтому за задачу поиск не ограничен, ограничен лишь «взрыв» поисков в одном ответе.
/// </para>
/// <para>
/// Экземпляр создаётся на прогон (в <c>RunLoopAsync</c>), а не живёт в сервисе: петля — свойство
/// конкретной задачи, и состояние не должно перетекать в следующий запуск.
/// </para>
/// </summary>
public sealed partial class AgentLoopGuard
{
    /// <summary>Сколько одинаковых вызовов с одинаковым результатом подряд считаются петлёй.</summary>
    public const int RepeatThreshold = 3;

    /// <summary>
    /// Сколько раз инструмент должен вернуть ОДИН И ТОТ ЖЕ результат (даже с разными аргументами),
    /// чтобы это считалось топтанием на месте: модель перебирает имена файлов и команды, а вывод не меняется.
    /// </summary>
    public const int NoProgressThreshold = 4;

    /// <summary>
    /// ROUTE_GUARD: сколько раз один и тот же КЛАСС ошибки (Repository not found, 403, auth failed…)
    /// разрешено получить при разных аргументах, прежде чем это считается петлёй.
    /// </summary>
    public const int ErrorNoProgressThreshold = 2;

    /// <summary>После стольких отказов подряд инструменты отключаются и модель обязана дать текстовый итог.</summary>
    public const int StopAfterRefusals = 3;

    /// <summary>
    /// Сколько раз можно обратиться к ОДНОМУ И ТОМУ ЖЕ объекту чтения (файл, шаблон, папка), пока не внесена
    /// правка. Агент один раз перечитывал один файл десятки раз с разными параметрами и не переходил к делу.
    /// </summary>
    public const int ReadTargetThreshold = 3;

    /// <summary>
    /// Сколько интернет-обращений (web_search + fetch_web_page) разрешено за ОДИН ход модели.
    /// Счётчик сбрасывается на каждом шаге цикла (<see cref="ResetWebBudget"/>), поэтому в целом за
    /// задачу поиск не ограничен — ограничен только «взрыв» поисков внутри одного ответа.
    /// </summary>
    public const int WebCallBudget = 10;

    /// <summary>Текст отказа при повторе. Модель читает его как результат инструмента.</summary>
    public const string RepeatRefusal =
        "Команда уже выполнялась несколько раз без изменения результата. Прекрати повторять одно и то же " +
        "действие. Проанализируй причину неудачи или заверши работу, объяснив пользователю проблему.";

    /// <summary>Текст отказа, когда результат не меняется от попытки к попытке.</summary>
    public const string NoProgressRefusal =
        "Ты уже получал ровно этот же результат несколько раз подряд, меняя только аргументы. Новых данных это не даст: " +
        "остановись, проанализируй, что уже известно, и либо сделай следующий шаг другим способом, либо дай итог пользователю.";

    /// <summary>
    /// ROUTE_GUARD: универсальный отказ. Не про GitHub и не про конкретный текст ошибки: если инструмент дважды
    /// вернул один и тот же класс отказа при РАЗНЫХ аргументах, причина не в аргументах. Раннер подставляет имя
    /// инструмента и список выданных альтернатив.
    /// </summary>
    public const string RouteRefusal =
        "[Route] Инструмент {tool} снова вернул тот же класс ошибки, хотя аргументы были другие. Причина не в " +
        "аргументах — не перебирай варианты. Смени маршрут: {alternatives}. Если факт проверить нечем — " +
        "спроси пользователя конкретно, что именно не так. {tool} заблокирован до успешного прогресса.";

    /// <summary>Текст отказа, когда модель проигнорировала несколько отказов подряд.</summary>
    /// <summary>Служебные инструменты: их повторы — не петля, а рабочка (план, вопрос, диагностика).</summary>
    private static readonly HashSet<string> BookkeepingTools = new(StringComparer.Ordinal)
    {
        "todo_write", "ask_user", "project_rules", "turn_diff", "read_skill",
        "agents_status", "team_message", "spawn_agents",
    };

    public const string LoopStopRefusal =
        "Инструменты отключены: ты повторял одни и те же действия без нового результата. Дай итоговый ответ текстом — " +
        "что сделано, что не получилось и что осталось. Не проси пользователя перезапускать программу или писать " +
        "«продолжай»: отказ инструментов — это конец этого хода, а не ваша задача для человека.";

    /// <summary>Текст отказа, когда один и тот же результат чтения получается снова и снова.</summary>
    public const string ReadTargetRefusal =
        "Ты уже получал ровно тот же результат чтения несколько раз — новых данных в этом объекте нет. " +
        "Дальше: если задача требует правки — примени прочитанное и измени файл; если это анализ — дай итог. " +
        "Другой файл, другая страница (offset) или другой шаблон не блокируются: новый результат — не петля.";

    /// <summary>
    /// WRITE_ONLY: после петли чтения инструменты не отключаются полностью — правка остаётся доступной.
    /// Раньше сообщение «инструменты отключены» заставляло модель сдаться, не попробовав правку.
    /// </summary>
    public const string WriteOnlyRefusal =
        "Хватит исследовать: чтение и команды остановлены до конца этого хода. Доступна только правка файлов " +
        "(file_patch, apply_patch, str_replace_editor, file_write). Сделай правку по уже собранному плану, " +
        "затем дай итог. Не пиши, что инструменты отключены: правка работает.";

    /// <summary>Текст отказа, когда исчерпан бюджет чтения за прогон.</summary>
    public const string ReadBudgetRefusal =
        "Бюджет чтения на этот запуск исчерпан. Больше не читай — действуй: правка файла или команда, затем проверка.";

    /// <summary>
    /// DOF_REPAIR: сообщение модели перед восстановительным ходом. Требование универсальное — не зависит ни
    /// от языка, ни от сборщика: правки должны дойти до проверенного состояния, прежде чем прогон закончится.
    /// </summary>
    public static string RepairTurnPrompt(bool verificationRan, string? files = null)
    {
        var state = verificationRan
            ? "Проверка уже запускалась и сейчас красная."
            : "Проверка ещё не запускалась.";
        var where = string.IsNullOrWhiteSpace(files) ? string.Empty : $" Изменённые файлы: {files}.";
        return "[Definition of Done] " + state + where +
               " Пока сборка/тесты не проходят, задача не выполнена: запусти проверку, исправь ошибки в " +
               "изменённых файлах, и только потом дай итог. Ошибку, вызванную твоей правкой, исправляй, " +
               "а не откатывай всё задание целиком.";
    }

    /// <summary>Текст отказа, когда интернет-бюджет этого хода исчерпан.</summary>
    public const string WebBudgetRefusal =
        "Лимит интернет-запросов на этот ход исчерпан. Не повторяй поиск сейчас — обработай уже " +
        "найденную информацию и продолжай задачу.";

    /// <summary>Инструменты чтения: повторный запрос к тому же объекту ничего не добавляет.</summary>
    public static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "file_read", "file_read_bytes", "grep", "glob", "workspace_list_files",
        "view_image", "read_document_file", "project_rules",
    };

    /// <summary>Оболочки: команду внутри ещё надо распознать как чтение или действие.</summary>
    public static bool IsShellTool(string tool) => tool is "bash" or "terminal_exec" or "cmd_exec";

    /// <summary>
    /// Читающая команда оболочки (Get-Content, Get-ChildItem, cat, head, git log…). Агент обходил защиту
    /// ровно так: десятки `bash Get-Content -Skip N` вместо инструмента чтения.
    /// </summary>
    public static bool IsReadOnlyCommand(string argumentsJson)
    {
        var command = ReadCommand(argumentsJson);
        if (string.IsNullOrWhiteSpace(command))
            return false;

        // PowerShell-скрипт: `$t=[IO.File]::ReadAllText(...); $t.Substring(...)` — цепочка, но только чтение.
        var lower = command.ToLowerInvariant();
        if (!ShellWriteTokens.Any(token => lower.Contains(token, StringComparison.Ordinal)) &&
            !lower.Contains(">>", StringComparison.Ordinal) && !lower.Contains(" > ", StringComparison.Ordinal) &&
            ShellReadTokens.Any(token => lower.Contains(token, StringComparison.Ordinal)))
        {
            return true;
        }

        // Запись, цепочки и подстановки — это уже действие, а не чтение.
        if (command.Contains('>') || command.Contains(';') || command.Contains("&&", StringComparison.Ordinal) ||
            command.Contains("||", StringComparison.Ordinal) || command.Contains("$(", StringComparison.Ordinal) ||
            command.Contains('`') || command.Contains('\n') || command.Contains('\r'))
            return false;
        return command.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(segment => SafeCommandHead().IsMatch(segment));
    }

    private static readonly string[] ShellReadTokens =
    {
        "get-content", "get-childitem", "gci ", "select-string", "findstr", "readalltext", "readalllines",
        "readallbytes", "get-item", "test-path", "resolve-path", "select-object", "measure-object",
        "out-string", "format-table", "format-list", "where-object", "sort-object", "convertto-json",
        "rg ", "grep", "cat ", "head ", "tail ", "wc ", "stat ", "ls ", "dir ",
        // READ_VIA_SHELL: прогон на реальном проекте — агент читал код десятками `sed -n '1,60p'`, и ни один
        // счётчик этого не видел: `sed` не было в списке. Чтение через оболочку — всё равно чтение.
        "sed ", "awk ", "nl ",
    };

    private static readonly string[] ShellWriteTokens =
    {
        "set-content", "add-content", "out-file", "remove-item", "new-item", "copy-item", "move-item",
        "rename-item", "set-item", "clear-content", "writealltext", "writeallbytes", "appendalltext",
        "::delete", "::move", "::copy", "start-process", "invoke-expression", "iex ", "dotnet ",
        "npm ", "yarn ", "pnpm ", "gradle ", "mvn ", "msbuild ", "git add", "git commit", "git push",
        "git checkout", "git reset", "git clean", "git stash", "del ", "erase ", "rmdir",
        // `sed -i` правит файл на месте.
        "sed -i", "sed --in-place",
    };

    private static string? ReadCommand(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("command", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?:get-content|gc|get-childitem|gci|dir|ls|cat|type|head|tail|grep|rg|select-string|sls|findstr|wc|where|which|pwd|tree|stat|file|sort|uniq|diff|find|git\s+(?:status|log|diff|show|branch|remote|rev-parse|ls-files|blame))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SafeCommandHead();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[A-Za-z]:)?[A-Za-z0-9_.\-/\\]+\.(?:cs|js|ts|tsx|jsx|css|html|htm|json|md|py|csproj|sln|txt|xml|yml|yaml|props|targets|ps1|sh|bat)")]
    private static partial System.Text.RegularExpressions.Regex FilePathInCommand();

    /// <summary>Инструменты, которые ходят в интернет и считаются в общий бюджет.</summary>
    private static readonly HashSet<string> WebTools = new(StringComparer.Ordinal)
    {
        "web_search",
        "fetch_web_page"
    };

    private readonly int _repeatThreshold;
    private readonly int _webCallBudget;
    private readonly int _windowSize;
    private readonly List<Call> _recent = new();
    private readonly Dictionary<string, int> _targets = new(StringComparer.Ordinal);
    // NEW_CONTENT: хэш последнего результата чтения по объекту. Повтор засчитывается только при том же
    // содержимом: чтение другой страницы большого файла — это новый контекст, а не топтание на месте.
    private readonly Dictionary<string, string> _targetHashes = new(StringComparer.Ordinal);
    // READ_INSIST: объекты, по которым отказ уже выдан — повтор по тому же объекту и есть петля.
    private readonly HashSet<string> _refusedReadTargets = new(StringComparer.Ordinal);
    // ROUTE_GUARD: инструменты, временно снятые с маршрута после повторяющегося отказа.
    private readonly HashSet<string> _blockedTools = new(StringComparer.Ordinal);
    // ROUTE_GUARD: класс последнего отказа и сколько раз он повторился ПОДРЯД. Успешный вызов обнуляет.
    private readonly Dictionary<string, string> _routeSignature = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _routeFailures = new(StringComparer.Ordinal);

    /// <summary>Инструменты, снятые с маршрута, — раннер не предлагает их как альтернативу.</summary>
    public IReadOnlyCollection<string> BlockedTools => _blockedTools;
    private bool _writeOnly;

    public AgentLoopGuard(
        int repeatThreshold = RepeatThreshold,
        int webCallBudget = WebCallBudget,
        int windowSize = 8)
    {
        _repeatThreshold = Math.Max(1, repeatThreshold);
        _webCallBudget = Math.Max(0, webCallBudget);
        // Окно не может быть короче порога повторов, иначе петлю не из чего было бы собрать.
        _windowSize = Math.Max(_repeatThreshold, windowSize);
    }

    /// <summary>Сколько интернет-обращений уже израсходовано в этом ходе.</summary>
    public int WebCallsUsed { get; private set; }

    /// <summary>Сколько вызовов отклонено из-за петли. По нему раннер решает отключить инструменты.</summary>
    public int Refusals { get; private set; }

    /// <summary>Пора заканчивать: модель проигнорировала отказы и продолжает топтаться на месте.</summary>
    public bool ShouldStop => Refusals >= StopAfterRefusals;

    /// <summary>Сейчас разрешена только правка файлов (после петли чтения).</summary>
    public bool WriteOnly => _writeOnly;

    /// <summary>
    /// Переход в режим «только правка»: счётчики сбрасываются, чтения и команды получают отказ,
    /// правка выполняется. Если модель проигнорирует и это (ещё 3 отказа) — завершаем ход текстом.
    /// </summary>
    public void EnterWriteOnly()
    {
        _writeOnly = true;
        Refusals = 0;
        _targets.Clear();
        _targetHashes.Clear();
        _refusedReadTargets.Clear();
        _recent.Clear();
    }

    /// <summary>Инструмент меняет файл — в режиме write-only только они и разрешены.</summary>
    public static bool IsWriteTool(string tool, string arguments)
    {
        if (tool is "file_write" or "file_patch" or "apply_patch" or "file_write_bytes" or "pc_copy_to_project"
            or "download_file" or "create_document")
            return true;
        if (tool != "str_replace_editor")
            return false;
        return ReadCommand(arguments) != "view";
    }

    /// <summary>
    /// Открывает новый бюджет интернет-обращений — вызывается в начале каждого хода модели. За один
    /// ответ (ход) разрешено не больше <see cref="WebCallBudget"/> обращений, но на следующем шаге
    /// счётчик снова полон: за задачу поиск не ограничен, ограничен лишь взрыв поисков внутри хода.
    /// </summary>
    public void ResetWebBudget() => WebCallsUsed = 0;

    /// <summary>
    /// Можно ли выполнять вызов. <c>null</c> — можно; иначе причина отказа, которую надо вернуть
    /// модели вместо результата инструмента. Для интернет-инструментов бюджет списывается здесь же,
    /// поэтому метод вызывается РОВНО один раз на вызов инструмента.
    /// </summary>
    public string? Refuse(string tool, string arguments)
    {
        // BOOKKEEPING: план, вопрос человеку и диагностика не участвуют в защите от петель — иначе обновление
        // плана с одинаковым ответом «План обновлён» считалось топтанием на месте и отключало все инструменты.
        if (BookkeepingTools.Contains(tool))
            return null;

        // WRITE_ONLY: после петли чтения разрешаем только правку; чтения и команды — отказ.
        if (_writeOnly)
        {
            if (IsWriteTool(tool, arguments))
                return null;
            Refusals++;
            return WriteOnlyRefusal;
        }

        // ROUTE_GUARD: чтение и поиск — путь к восстановлению контекста. Блокировать их после серии отказов
        // значит запереть модель без возможности перечитать файл и исправить правку.
        if (IsRead(tool, arguments) && _targets.GetValueOrDefault(Target(tool, arguments)) >= ReadTargetThreshold)
        {
            // READ_INSIST: первый отказ по этому объекту — просто подсказка, а не повод отключать
            // инструменты. К остановке ведёт только НАСТОЯНИЕ: повторное чтение того же объекта.
            if (!_refusedReadTargets.Add(Target(tool, arguments)))
                Refusals++;
            return ReadTargetRefusal;
        }

        if (RefusesRepeat(tool, arguments))
        {
            Refusals++;
            return RepeatRefusal;
        }

        // ROUTE_GUARD: тот же класс ошибки при других аргументах — менять надо маршрут, а не написание.
        if (!IsRead(tool, arguments) && RefusesRoute(tool))
        {
            // ROUTE_GUARD: инструмент блокируется до первого успешного вызова — иначе модель вернётся к нему
            // через ход и снова будет перебирать аргументы.
            _blockedTools.Add(tool);
            Refusals++;
            return RouteRefusal;
        }

        // Разные аргументы, но один и тот же результат: перебор имён файлов и вариантов команды.
        if (RefusesNoProgress(tool))
        {
            Refusals++;
            return NoProgressRefusal;
        }

        if (WebTools.Contains(tool))
        {
            if (WebCallsUsed >= _webCallBudget)
            {
                return WebBudgetRefusal;
            }

            WebCallsUsed++;
        }

        return null;
    }

    /// <summary>
    /// Запоминает фактический результат — только по нему и видно, что повтор ничего не меняет
    /// (упавшая команда и та же команда с новым выводом — разные ситуации).
    /// </summary>
    /// <summary>Отказ по бюджету чтения: раннер сам решает, когда чтения стало слишком много.</summary>
    public string RefuseRead()
    {
        Refusals++;
        return ReadBudgetRefusal;
    }

    /// <summary>
    /// DOF_REPAIR: снять все ограничения перед одним завершающим ходом проверки. Нужен, когда правки уже
    /// сделаны, но сборка/тесты ещё не проходили: запереть модель отказами значит закончить прогон
    /// некомпилируемым кодом.
    /// </summary>
    public void ResetForRepair()
    {
        _writeOnly = false;
        Refusals = 0;
        _targets.Clear();
        _targetHashes.Clear();
        _refusedReadTargets.Clear();
        _recent.Clear();
        _blockedTools.Clear();
        _routeSignature.Clear();
        _routeFailures.Clear();
    }

    /// <summary>Выход из режима «только правка»: чтение и команды снова разрешены.</summary>
    private void ExitWriteOnly()
    {
        _writeOnly = false;
        Refusals = 0;
        _targets.Clear();
        _targetHashes.Clear();
        _refusedReadTargets.Clear();
    }

    /// <param name="isError">Инструмент вернул ошибку. Только ошибки формируют сигнатуру отказа: обычный
    /// вывод может повторяться («ok»), и это не повод менять маршрут.</param>
    public void Record(string tool, string arguments, string output, bool isError = false)
    {
        // BOOKKEEPING: и в историю вызовов не пишем — иначе один и тот же текст отчёта плана ломал
        // сравнение «одинаковый результат» для следующих настоящих инструментов.
        if (BookkeepingTools.Contains(tool))
            return;
        // WRITE_ONLY: успешная правка — прогресс, режим снимается и счётчики обнуляются.
        if (_writeOnly && IsWriteTool(tool, arguments))
        {
            if (!isError)
            {
                ExitWriteOnly();
                _recent.Clear();
                return;
            }
            // BLOCKED_CONTEXT: «контекст не найден» — не отсутствие прогресса, а устаревший фрагмент.
            // В режиме «только правка» модель не может даже перечитать файл, поэтому выходим из него.
            if (IsFixableEditError(tool, output))
                ExitWriteOnly();
        }
        // ROUTE_GUARD: успешный вызов инструмента — это прогресс: снимаем блокировку и обнуляем серию отказов.
        if (!isError)
        {
            _blockedTools.Remove(tool);
            _routeSignature.Remove(tool);
            _routeFailures.Remove(tool);
        }
        else if (ErrorSignature(output) is { } signature)
        {
            // FIXABLE_EDIT: «контекст не найден» / «хунк не применился» лечится перечитыванием файла.
            // Если считать это повторяющимся отказом класса, инструмент правки блокируется — и агент
            // остаётся с некомпилируемым файлом без единого способа его починить.
            if (IsFixableEditError(tool, output))
            {
                _routeSignature.Remove(tool);
                _routeFailures.Remove(tool);
            }
            // Считаем подряд идущие отказы ОДНОГО класса: если класс сменился, это новая попытка, а не петля.
            else if (_routeSignature.GetValueOrDefault(tool) == signature)
                _routeFailures[tool] = _routeFailures.GetValueOrDefault(tool) + 1;
            else
            {
                _routeSignature[tool] = signature;
                _routeFailures[tool] = 1;
            }
        }
        _recent.Add(new Call(tool, arguments, Hash(output), isError ? ErrorSignature(output) : null));
        // Любая правка/команда — прогресс: счётчики чтения обнуляются, перечитывать после правки нормально.
        if (IsProgressTool(tool, arguments))
        {
            _targets.Clear();
            _targetHashes.Clear();
        }
        else if (IsRead(tool, arguments))
        {
            var key = Target(tool, arguments);
            var hash = Hash(output);
            // NEW_CONTENT: тот же объект с ДРУГИМ результатом (другая страница большого файла, файл изменился
            // на диске) — это новый контекст. Повтор засчитывается только при том же выводе.
            _targets[key] = _targetHashes.GetValueOrDefault(key) == hash
                ? _targets.GetValueOrDefault(key) + 1
                : 1;
            _targetHashes[key] = hash;
        }

        if (_recent.Count > _windowSize)
        {
            _recent.RemoveRange(0, _recent.Count - _windowSize);
        }
    }

    /// <summary>
    /// Последние <see cref="NoProgressThreshold"/> результатов ЭТОГО инструмента совпали, даже если аргументы
    /// менялись. Вызовы других инструментов между ними не рвут цепочку: в петле модель обычно чередует
    /// «записать → пересчитать → прочитать» с одним и тем же выводом.
    /// </summary>
    public bool RefusesNoProgress(string tool)
    {
        var seen = 0;
        string? output = null;
        for (var i = _recent.Count - 1; i >= 0 && seen < NoProgressThreshold; i--)
        {
            var call = _recent[i];
            if (!string.Equals(call.Tool, tool, StringComparison.Ordinal))
                continue;
            output ??= call.OutputHash;
            if (!string.Equals(call.OutputHash, output, StringComparison.Ordinal))
                return false;
            seen++;
        }
        return seen >= NoProgressThreshold;
    }

    /// <summary>
    /// ROUTE_GUARD: два одинаковых ПО СМЫСЛУ отказа (Repository not found, 403, auth failed…) при разных
    /// аргументах — уже петля: третий заход с новым написанием адреса ничего не даст, пора менять маршрут.
    /// </summary>
    public bool RefusesRoute(string tool) =>
        _blockedTools.Contains(tool) || _routeFailures.GetValueOrDefault(tool) >= ErrorNoProgressThreshold;

    /// <summary>
    /// Три (и больше) одинаковых вызова ПОДРЯД, и все они дали один и тот же вывод. Любой другой
    /// вызов между ними рвёт цепочку: «упало → поправил → снова упало» — это нормальная работа,
    /// а не петля.
    /// </summary>
    public bool RefusesRepeat(string tool, string arguments)
    {
        var matching = 0;
        string? output = null;

        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var call = _recent[i];
            if (!string.Equals(call.Tool, tool, StringComparison.Ordinal) ||
                !string.Equals(call.Arguments, arguments, StringComparison.Ordinal))
            {
                break;
            }

            output ??= call.OutputHash;
            if (!string.Equals(call.OutputHash, output, StringComparison.Ordinal))
            {
                // Вывод менялся от попытки к попытке — это попытки, а не петля.
                return false;
            }

            matching++;
        }

        return matching >= _repeatThreshold;
    }

    /// <summary>Новый ли это объект чтения (ещё не читали в этом прогоне). Новый файл — прогресс, не петля.</summary>
    public bool IsNewReadTarget(string tool, string arguments) =>
        IsRead(tool, arguments) && !_targets.ContainsKey(Target(tool, arguments));

    /// <summary>Инструменты, меняющие файл (внутренние имена).</summary>
    public static bool IsEditTool(string tool) =>
        tool is "str_replace_editor" or "apply_patch" or "file_patch" or "file_write";

    /// <summary>
    /// Ошибки правки, которые лечатся перечитыванием файла, а не сменой маршрута. Это ответ инструмента
    /// модели («фрагмент не совпал»), а не отказ среды — блокировать за него инструмент нельзя.
    /// </summary>
    private static readonly string[] FixableEditMarkers =
    {
        "контекст не найден", "context not found", "not found in file", "old_string", "old_str",
        "hunk", "не удалось применить", "failed to apply", "no match", "не совпадает", "ambiguous",
    };

    /// <summary>Ошибку правки можно исправить повторным чтением файла, инструмент за неё не блокируется.</summary>
    public static bool IsFixableEditError(string tool, string output)
    {
        if (!IsEditTool(tool) || string.IsNullOrEmpty(output))
            return false;
        var lower = output.ToLowerInvariant();
        foreach (var marker in FixableEditMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Чтение — либо инструмент чтения, либо читающая команда оболочки.
    /// </summary>
    public static bool IsRead(string tool, string arguments) =>
        ReadTools.Contains(tool) || IsShellTool(tool) && IsReadOnlyCommand(arguments);

    /// <summary>Ключ объекта чтения без пагинации: тот же файл с другим view_range — тот же объект.</summary>
    private static string Target(string tool, string arguments)
    {
        if (IsShellTool(tool))
        {
            // Объект — файл из команды, а не текст скрипта: ReadAllText/ReadAllLines/Select-String одного
            // файла — одно и то же чтение, сколько бы разных однострочников модель ни придумала.
            var command = ReadCommand(arguments) ?? arguments;
            var file = FilePathInCommand().Match(command);
            return tool + "|" + (file.Success
                ? file.Value.Replace('\\', '/').ToLowerInvariant()
                : NormalizeCommand(command));
        }
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return tool + "|" + arguments;
            var parts = new List<string>();
            // READ_PAGING: у file_read пагинация — штатный способ читать большой файл, и разные
            // страницы — разная информация. Без этого агент не мог прочитать index.html целиком
            // (третий offset считался той же самой «петлёй»).
            var keepPaging = tool is "file_read" or "file_read_bytes";
            foreach (var property in doc.RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!keepPaging && PagingKeys.Contains(property.Name))
                    continue;
                parts.Add(property.Name + "=" + property.Value.GetRawText());
            }
            return tool + "|" + string.Join(";", parts);
        }
        catch (JsonException)
        {
            return tool + "|" + arguments;
        }
    }

    /// <summary>
    /// Одинаковые по сути читающие команды: `Get-Content f -Skip 100 -TotalCount 100` и `-Skip 200` —
    /// тот же файл. Поэтому убираем числа, лишние пробелы и кавычки.
    /// </summary>
    private static string NormalizeCommand(string command)
    {
        var sb = new StringBuilder(command.Length);
        var space = false;
        foreach (var c in command.ToLowerInvariant())
        {
            if (char.IsDigit(c) || c is '\'' or '"')
                continue;
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }
            sb.Append(c);
            space = false;
        }

        // Флаги пагинации не делают команду другой: `-Skip`, `-TotalCount`, `head`/`tail` — всё то же чтение.
        var kept = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !PagingTokens.Contains(token));
        return string.Join(' ', kept);
    }

    private static readonly HashSet<string> PagingTokens = new(StringComparer.Ordinal)
    {
        "-skip", "-first", "-last", "-totalcount", "-n", "-c", "-l", "-lines", "-offset", "-limit",
        "--skip", "--first", "--last", "--limit", "--offset", "--lines", "head", "tail",
    };

    private static readonly HashSet<string> PagingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "view_range", "offset", "limit", "max_results", "lines", "tail_lines", "head_lines",
        "context_lines", "start", "end", "from", "to", "length", "start_line", "end_line", "count",
    };

    /// <summary>Правка или команда — значит, работа идёт, и счётчики чтения можно сбросить.</summary>
    public static bool IsProgressTool(string tool, string arguments) => tool switch
    {
        "file_write" or "file_write_bytes" or "file_patch" or "apply_patch" => true,
        // Запуск сборки/тестов — действие, а `Get-Content`/`Get-ChildItem` — всё ещё чтение.
        "bash" or "terminal_exec" or "cmd_exec" => !IsReadOnlyCommand(arguments),
        "run_tests" or "get_diagnostics" => true,
        "bg_start" or "bg_stop" or "install_software" => true,
        "str_replace_editor" => !arguments.Contains("\"view\"", StringComparison.Ordinal),
        _ => false,
    };

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ROUTE_GUARD: одинаковый КЛАСС ошибки при разных аргументах — это не перебор вариантов, а топтание на
    // месте: агент угадывал имя репозитория (`rem1xzv/ConoxyAI`, `biocompass`…), получал один и тот же
    // «Repository not found» и продолжал. Хеш вывода не ловил это: текст ошибки PowerShell содержит URL с
    // очередной догадкой. Сигнатура убирает изменчивое (адреса, числа, знаки) — остаётся смысл ошибки.
    /// <summary>
    /// ROUTE_GUARD: классы отказов, общие для любого инструмента. Сигнатура — НАБОР классов, а не текст:
    /// имя репозитория, файла или адреса в тексте меняется, а причина остаётся той же.
    /// </summary>
    private static readonly (string Kind, string[] Markers)[] FailureClasses =
    {
        ("not_found", ["not found", "no such", "does not exist", "cannot find", "could not find", "unable to find", "не найден", "не существует", "нет такого"]),
        ("denied", ["permission denied", "access denied", "access is denied", "forbidden", "403", "отказано в доступе", "запрещен"]),
        ("auth", ["unauthorized", "authentication", "credential", "not logged in", "401", "authoriz", "авториз", "логин"]),
        ("timeout", ["timed out", "timeout", "time out", "превышено время"]),
        ("network", ["connection refused", "connection reset", "could not resolve", "name resolution", "unreachable", "network is unreachable", "econnrefused", "no internet", "сеть недоступна"]),
        ("tls", ["ssl", "certificate", "tls handshake"]),
        ("rate_limit", ["rate limit", "too many requests", "429", "quota"]),
        ("conflict", ["merge conflict", "conflict", "конфликт"]),
        ("exists", ["already exists", "already in use", "уже существует"]),
        ("disk", ["no space left", "disk full", "read-only file system", "нет места"]),
    };

    private static string? ErrorSignature(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lower = output.ToLowerInvariant();
        var kinds = FailureClasses
            .Where(cls => cls.Markers.Any(marker => lower.Contains(marker, StringComparison.Ordinal)))
            .Select(cls => cls.Kind)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToList();
        return kinds.Count == 0 ? null : string.Join("+", kinds);
    }

    private readonly record struct Call(string Tool, string Arguments, string OutputHash, string? ErrorSignature = null);
}
