using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Tools;

// COMMAND_TIMEOUTS: добавлено 2026-11-20 — копия серверной политики (ConexyAI/Service/CommandTimeoutPolicy)
// для десктопного клиента: проект Desktop не ссылается на серверную сборку. Держать таблицу одинаковой:
// интерактив 120 с, тесты/сборки 3600 с, остальное 300 с; явный timeout_seconds главнее.
public enum CommandDuration
{
    Interactive,
    Long,
    Normal,
}

public static class CommandTimeoutPolicy
{
    public const int InteractiveSeconds = 120;
    public const int LongSeconds = 3_600;
    public const int NormalSeconds = 300;
    public const int MaxSeconds = 3_600;

    private static readonly Regex LongPattern = new(
        @"(?i)(\bpytest\b|\bunittest\b|\btox\b|\bnox\b|\bvitest\b|\bjest\b|\bmocha\b|\bplaywright\s+(?:install|test)\b|" +
        @"\bdotnet\s+(?:test|build|restore|publish|pack|run|workload)\b|\bnpm\s+(?:ci|install|i|run|test|start|build|exec)\b|" +
        @"\byarn\s+(?:install|add|run|test|build)\b|\bpnpm\s+(?:install|add|run|test|build)\b|" +
        @"\bcargo\s+(?:build|test|check|run|fetch|install)\b|\bgo\s+(?:build|test|run|mod|get)\b|" +
        @"\bmvn\b|\bgradle\b|gradlew\b|\bcmake\b|\bmake\b|\bmsbuild\b|\bnuget\s+restore\b|" +
        @"\bdocker\s+(?:build|compose\s+(?:up|build|pull)|pull|push|run)\b|\bdocker-compose\b|" +
        @"\bpip3?\s+(?:install|download)\b|\bpoetry\s+install\b|\bbundle\s+install\b|\bcomposer\s+install\b|" +
        @"\bgit\s+(?:clone|fetch|pull|push)\b|\bwinget\s+(?:install|upgrade)\b|\bchoco\s+install\b|\bbrew\s+install\b|" +
        @"\bapt(?:-get)?\s+install\b|\bcurl\s+-[^\s]*O|\bwget\b|\bpnpm\s+dlx\b|--build\b)");

    private static readonly Regex InteractiveVerbs = new(
        @"(?i)^\s*(?:sudo|su|passwd|ssh|scp|sftp|ftp|telnet|nc|ncat|mysql|psql|sqlite3|mongo|mongosh|redis-cli|" +
        @"read|pause|Read-Host|Get-Credential|Wait-Event)\b|" +
        @"\bgit\s+commit\b(?!.*\s(?:-m|--message|-F|--file)\b)|" +
        @"\bgit\s+rebase\b(?!.*--(?:no-)?interactive)|" +
        @"\bnpm\s+init\b(?!.*\s-y\b)|\bnpx\s+(?:create|degit)\b(?!.*--yes)|" +
        @"\bGet-Content\s+[^\n]*-Wait\b|\btail\s+-f\b|\bStart-Process\b(?!.*-Wait\b)",
        RegexOptions.Compiled);

    // INTERACTIVE_REPL: интерпретатор без скрипта (или с -i) — это REPL, он ждёт ввод.
    private static readonly Regex InteractiveRepl = new(
        @"(?i)^\s*(?:python\d?|python3?|py|node|pwsh|powershell|cmd|sh|bash|irb|ghci)\s*(?:-i|--interactive)?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex PromptWait = new(
        @"(?i)(password\s*:|passphrase\s*:|enter\s+(?:the\s+)?(?:password|passphrase|username|value|name)|" +
        @"are\s+you\s+sure|continue\?\s*(?:\[|\(|y/n)|press\s+(?:any\s+)?key|proceed\?|" +
        @"\[sudo\]\s*password|waiting\s+for\s+(?:input|response)|login\s+as\s*:|" +
        @"eof(?:error|exception)|cannot\s+read\s+from\s+stdin|stdin\s+is\s+not\s+a\s+tty|not\s+a\s+terminal|" +
        @"input\(\):\s*lost\s+sys\.stdin|unexpected\s+end\s+of\s+input)");

    public static CommandDuration Classify(string? command)
    {
        var value = command ?? string.Empty;
        if (LongPattern.IsMatch(value))
            return CommandDuration.Long;
        if (InteractiveVerbs.IsMatch(value) || InteractiveRepl.IsMatch(value))
            return CommandDuration.Interactive;
        return CommandDuration.Normal;
    }

    public static int Resolve(string? command, int? requestedSeconds)
    {
        if (requestedSeconds is > 0)
            return Math.Clamp(requestedSeconds.Value, 1, MaxSeconds);
        return Classify(command) switch
        {
            CommandDuration.Interactive => InteractiveSeconds,
            CommandDuration.Long => LongSeconds,
            _ => NormalSeconds,
        };
    }

    public static string TimeoutMessage(string? command, int seconds) =>
        $"[Команда прервана по таймауту {seconds} с] Инструменты и дальше доступны. " +
        "Если команда ждала ввод/пароль/подтверждение — stdin у неё закрыт: передай неинтерактивные флаги " +
        "(`-y`, `--yes`, `--non-interactive`, `-m \"...\"` для git commit, `-c`/`-e` для SQL), запусти её в фоне " +
        "(`bg_start`), или спроси пользователя через `ask_user`. Для тестов/сборок укажи `timeout_seconds` явно (до 3600). " +
        "Не повторяй ту же команду без изменений.";

    public static bool WaitedForInput(string? output) =>
        !string.IsNullOrWhiteSpace(output) && PromptWait.IsMatch(output);

    public static string? InputWaitingHint(string? output) =>
        WaitedForInput(output)
            ? "[Подсказка] Похоже, команда ждала интерактивный ввод (пароль/подтверждение/строку). " +
              "stdin закрыт, поэтому она не может ждать: передай неинтерактивные флаги (`-y`, `--yes`, " +
              "`--non-interactive`, `-m \"...\"` для git commit, `-c`/`-e` для SQL), либо спроси пользователя через `ask_user`."
            : null;
}
