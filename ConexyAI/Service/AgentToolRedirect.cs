using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// P0_TOOL_REDIRECT: добавлено 2026-10-10 — разгрузка перекрывающихся инструментов. Модель по
// привычке решает задачи shell-командами (`bash: git push`, `bash: npm test`, `bash: tsc`), хотя для
// этого есть профильные инструменты с токеном пользователя и структурированным ответом. Здесь —
// чистый, тестируемый классификатор: он либо разрешает команду (null), либо отдаёт КОНКРЕТНУЮ
// инструкцию, какой инструмент использовать вместо неё. Это и есть «действие-ориентированная ошибка»
// вместо вагого отказа, из-за которого агент ходил кругами.
public static class AgentToolRedirect
{
    // Сегменты команды: по `&&`, `||`, `;`, `|` и переводам строк. Одиночный `&` не трогаем — он
    // встречается в URL, а `cmd1 && cmd2` и так разберётся в две части.
    private static readonly Regex SegmentSplitter = new(@"\s*(?:&&|\|\||;|\||\n)\s*", RegexOptions.Compiled);

    // Ведущие обёртки, которые не меняют сути команды.
    private static readonly Regex Wrapper = new(
        @"^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*(?:(?:sudo|doas|command|nohup|env)\s+)*(?:timeout\s+\S+\s+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CredentialedClone = new(
        @"://[^/@\s]+:[^/@\s]+@|x-access-token|gh[pousr]_[A-Za-z0-9]|github_pat_|http\.extraheader|extraheader\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CredentialConfig = new(
        @"credential|extraheader|insteadof",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns an actionable refusal message when <paramref name="command"/> should go through a
    /// dedicated tool instead of <c>bash</c>, or <c>null</c> when the command is fine to run.
    /// </summary>
    public static string? RefusedBashCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        foreach (var raw in SegmentSplitter.Split(command))
        {
            var segment = Wrapper.Replace(raw, string.Empty).Trim();
            if (segment.Length == 0) continue;

            var words = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            var refusal = Classify(segment, words);
            if (refusal is not null) return refusal;
        }

        return null;
    }

    private static string? Classify(string segment, string[] words)
    {
        var first = words[0].ToLowerInvariant();

        if (first == "gh") return GithubRedirect(segment);

        if (first == "git")
        {
            var sub = GitSubcommand(words);
            return sub switch
            {
                "push" or "pull" or "fetch" or "remote" or "submodule" or "ls-remote" or "credential" =>
                    GithubRedirect(segment),
                "clone" when CredentialedClone.IsMatch(segment) => CredentialSmugglingRedirect(segment),
                "config" when CredentialConfig.IsMatch(segment) => CredentialSmugglingRedirect(segment),
                _ => null,
            };
        }

        if (IsTestCommand(first, words)) return TestsRedirect(segment);
        if (IsCheckCommand(first, words)) return ChecksRedirect(segment);
        return null;
    }

    /// <summary>Подкоманда `git`, пропуская опции и их значения (`git -C dir push` → `push`).</summary>
    private static string GitSubcommand(string[] words)
    {
        for (var i = 1; i < words.Length; i++)
        {
            var w = words[i];
            if (w.StartsWith('-'))
            {
                // Опции, за которыми идёт отдельное значение.
                if (w is "-C" or "--git-dir" or "--work-tree" or "-c" or "--exec-path" or "--namespace")
                    i++;
                continue;
            }
            return w.ToLowerInvariant();
        }
        return string.Empty;
    }

    private static bool IsTestCommand(string first, string[] words)
    {
        if (first is "pytest" or "jest" or "vitest" or "mocha" or "rspec" or "phpunit")
            return true;

        if (first is "npm" or "yarn" or "pnpm" or "bun")
        {
            var second = Second(words);
            var third = Third(words);
            if (second == "test") return true;
            if (second == "run" && third.StartsWith("test", StringComparison.Ordinal)) return true;
        }

        if ((first is "dotnet" or "go" or "cargo") && Second(words) == "test")
            return true;

        if ((first is "python" or "python3") && Regex.IsMatch(string.Join(' ', words), @"-m\s+pytest\b"))
            return true;

        return false;
    }

    private static bool IsCheckCommand(string first, string[] words)
    {
        if (first is "tsc" or "eslint" or "mypy" or "ruff" or "pyright")
            return true;

        var second = Second(words);
        var third = Third(words);

        if (first == "npx" && (second is "tsc" or "eslint" or "pyright"))
            return true;

        if ((first is "pnpm" or "yarn" or "bun") && (second is "tsc" or "eslint" or "pyright"))
            return true;

        if (first == "npm" && second == "run" && (third is "lint" or "typecheck" or "tsc" or "eslint"))
            return true;

        if ((first is "yarn" or "pnpm" or "bun") && second == "run" && (third is "lint" or "typecheck" or "tsc" or "eslint"))
            return true;

        // `dotnet build` НЕ трогаем: это каноничная сборка .NET, на которой держится гейт
        // самокоррекции (см. BuildHealth/VerificationCommands) и его тесты. Полноценные сборки-
        // артефакты (`npm run build`, `cargo build`, `go build`, `dotnet publish`) — тоже остаются
        // в bash: они и собирают, и дают гейту сигнал. Редиректятся только чистые проверки.
        if (first == "cargo" && second == "check")
            return true;

        if (first == "go" && second == "vet")
            return true;

        return false;
    }

    private static string Second(string[] words) => words.Length > 1 ? words[1].ToLowerInvariant() : string.Empty;

    private static string Third(string[] words) => words.Length > 2 ? words[2].ToLowerInvariant() : string.Empty;

    private static string GithubRedirect(string command) =>
        $"Отклонено: `{command}` — у песочницы нет git-креденшелов (это by design), поэтому операции с " +
        "GitHub здесь невозможны. Используй инструмент `github_action` (clone_repo, create_branch, " +
        "switch_branch, commit_and_push, create_pull_request, delete_branch) — он работает с Personal " +
        "Access Token пользователя, который сервер подставляет сам. Для чтения GitHub без клона " +
        "(issues, PR, ветки, CI) используй `github_api`. Не ищи токен в bash/env и не пробуй обойти это.";

    private static string CredentialSmugglingRedirect(string command) =>
        $"Отклонено: `{command}` — креды нельзя подсовывать в песочницу. Убери токен из URL/конфига git и " +
        "выполни операцию через `github_action` (он сам приложит токен пользователя).";

    private static string TestsRedirect(string command) =>
        $"Отклонено: прогон тестов через `bash` (`{command}`) не нужен. Вызови инструмент `run_tests` — он " +
        "сам определит фреймворк и вернёт структурированный отчёт (pass/fail, файл:строка, ожидал/получил) " +
        "вместо сырых логов. Если нужна своя команда, передай её в `run_tests.command`.";

    private static string ChecksRedirect(string command) =>
        $"Отклонено: проверки сборки/типов через `bash` (`{command}`) не нужны. Вызови `get_diagnostics` — он " +
        "подберёт проверку (dotnet build, tsc, eslint, cargo check, go vet, mypy/ruff/pyright) и вернёт " +
        "`severity: файл:строка:колонка [код] сообщение` для каждой ошибки. Своя команда — `get_diagnostics.command`.";
}
