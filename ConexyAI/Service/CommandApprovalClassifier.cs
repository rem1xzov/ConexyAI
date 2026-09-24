using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// COMMAND_APPROVAL: добавлено 2026-09-22
/// <summary>
/// Decides whether an agent <c>bash</c> command needs the user's explicit approval before it runs.
/// <para>
/// Previously every single command opened a full-size confirmation card, so a normal task turned
/// into a wall of identical cards (ls, cat, git status, grep …) and the reasoning text between the
/// steps was lost. Read-only diagnostics now run straight away and show up as one compact row;
/// only mutating / long / destructive commands pause for a decision.
/// </para>
/// </summary>
public interface ICommandApprovalClassifier
{
    /// <summary>
    /// True when the command must be confirmed by the user. Unknown or compound commands default
    /// to <c>true</c> (fail closed) — only commands positively recognised as read-only are exempt.
    /// </summary>
    bool RequiresApproval(string command);
}

public class CommandApprovalClassifier : ICommandApprovalClassifier
{
    private readonly IDangerousCommandClassifier _dangerous;

    public CommandApprovalClassifier(IDangerousCommandClassifier dangerous)
    {
        _dangerous = dangerous;
    }

    // COMMAND_APPROVAL: переписано 2026-09-24 — ревью H2.
    //
    // Обход был тривиальным: разделители цепочки не включали перевод строки и одиночный `&`, якорь `^`
    // без Multiline проверял только начало всей строки, а делили команду только по `|`. Поэтому
    // `ls\nrm -rf x` целиком считался безопасным `ls` и выполнялся без карточки. Плюс «безопасные»
    // утилиты с мутирующими флагами (`find -delete/-exec`, `sort -o`, `rg --pre`, `git branch -D`,
    // `env <любая команда>`) проходили без подтверждения.
    //
    // Теперь: ЛЮБОЙ управляющий оператор оболочки кроме простого пайпа (перевод строки, `;`, `&`,
    // `&&`, `||`, перенаправления, подстановки команд и процессов, here-doc) — сразу подтверждение;
    // каждый сегмент пайпа должен целиком совпасть с белым списком (якоря \A…\z), а у утилит из
    // белого списка отдельно запрещены пишущие/исполняющие флаги.

    /// <summary>
    /// Shell syntax that can run a second command, write a file or substitute output. Anything here
    /// means the command is not a plain read-only pipeline.
    /// </summary>
    private static readonly Regex ControlSyntax = new(
        @"[\r\n;&<>`]|\|\||\$\(|\$\{|\\$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Read-only commands that are safe to run without asking. Each pattern must match a WHOLE
    // pipeline segment (\A … \z), so nothing can hide after the recognised prefix.
    private static readonly Regex[] SafeSegmentPatterns =
    {
        // Listing / inspecting / reading.
        Segment(@"(ls|dir|pwd|cat|head|tail|wc|stat|du|df|realpath|basename|dirname|nl|column|readlink)"),
        Segment(@"tree"),
        Segment(@"file"),
        // Searching.
        Segment(@"(grep|egrep|fgrep|ag)"),
        Segment(@"rg"),
        Segment(@"fd"),
        Segment(@"find"),
        // Text processing that only reads.
        Segment(@"(cut|tr|jq|diff|comm|strings)"),
        Segment(@"sort"),
        Segment(@"uniq"),
        // `sed -n 'N,Mp' file` only — sed scripts can write (w) and execute (e).
        new Regex(@"\Ased\s+-n\s+'?\d+(,\d+)?p'?(\s+[^\s|]+)*\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // Environment / metadata (no arguments that could run or change anything).
        Segment(@"(which|where|whoami|id|date|uname|type|printenv)"),
        new Regex(@"\A(hostname|env)\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new Regex(@"\Acommand\s+-v\s+[\w.-]+\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        // Git porcelain commands that never change the repository.
        Segment(@"git\s+(status|log|diff|show|rev-parse|ls-files|blame|describe|shortlog|whatchanged)"),
        new Regex(@"\Agit\s+branch(\s+(-a|-r|-v|-vv|--list|--all|--remotes|--show-current|--no-color))*\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new Regex(@"\Agit\s+remote(\s+-v|\s+show(\s+[\w.-]+)?|\s+get-url\s+[\w.-]+)?\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new Regex(@"\Agit\s+stash\s+list\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new Regex(@"\Agit\s+config\s+--get\s+[\w.-]+\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new Regex(@"\A(git|dotnet|npm|pnpm|yarn|node|python3?|go|java|rustc|cargo|tsc|pip3?)\s+(--version|-v|-V|version)\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new Regex(@"\Adotnet\s+(--info|--list-sdks|--list-runtimes)\s*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        Segment(@"(npm|pnpm|yarn)\s+(ls|list|view|outdated)"),
        Segment(@"echo"),
    };

    /// <summary>
    /// Per tool: flags that turn an otherwise read-only tool into one that writes files or runs
    /// programs. Checked on every segment that matched the safe list.
    /// </summary>
    private static readonly (Regex Tool, Regex Flags)[] UnsafeFlags =
    {
        (Tool("find"), Flags(@"-delete|-exec|-execdir|-ok|-okdir|-fprint0?|-fprintf|-fls")),
        (Tool("fd"), Flags(@"-x|-X|--exec|--exec-batch|-[a-zA-Z]*[xX]")),
        (Tool("rg"), Flags(@"--pre(=\S*)?|--pre-glob(=\S*)?")),
        (Tool("sort"), Flags(@"-o\S*|--output(=\S*)?|--compress-program(=\S*)?")),
        (Tool("tree"), Flags(@"-o|--output(=\S*)?")),
        (Tool("file"), Flags(@"-C|--compile")),
        (Tool("git"), Flags(@"-c|--ext-diff|--textconv|--output(=\S*)?|--exec-path(=\S*)?|--config-env(=\S*)?")),
    };

    private static Regex Tool(string name) =>
        new(@"\A" + name + @"(\s|\z)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex Flags(string alternatives) =>
        new(@"(\s)(" + alternatives + @")(?=\s|\z)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Anything that can write, delete, network-fetch or change permissions is not "read-only",
    // even when it is not violent enough for the dangerous list.
    private static readonly Regex MutatingPattern =
        new(
            @"\b(rm|rmdir|mv|cp|touch|mkdir|ln|tee|dd|truncate|chmod|chown|chgrp|install|apt|apt-get|yum|brew|pip|pip3|curl|wget|xargs|git\s+(add|commit|push|pull|fetch|checkout|switch|reset|clean|merge|rebase|stash\s+(push|pop|drop|apply|clear)|tag|init|clone|restore|rm|mv)|npm\s+(i|install|ci|uninstall|update|exec|run|start|test)|npx|pnpm\s+(i|install|add|remove|exec|run|dlx)|yarn\s+(add|install|remove|run|dlx)|dotnet\s+(add|restore|nuget|publish|run|build|test|tool)|cargo\s+(build|test|run|add|install)|docker|kubectl|systemctl|service|sudo|sed\s+-i)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool RequiresApproval(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return true;

        // Destructive patterns always win, whatever the safe list thinks.
        if (_dangerous.IsDangerous(command))
            return true;

        // A second command (newline, ;, &, &&, ||), a redirect, a here-doc or a substitution can
        // smuggle a write into an otherwise harmless command, so none of those is read-only.
        if (ControlSyntax.IsMatch(command))
            return true;

        if (MutatingPattern.IsMatch(command))
            return true;

        // Every segment of the pipeline must itself be a known read-only command.
        var segments = command.Split('|');
        foreach (var raw in segments)
        {
            var segment = raw.Trim();
            if (segment.Length == 0)
                return true; // `a | | b` or a trailing pipe: malformed, do not guess.

            if (!SafeSegmentPatterns.Any(p => p.IsMatch(segment)))
                return true;

            if (UnsafeFlags.Any(u => u.Tool.IsMatch(segment) && u.Flags.IsMatch(segment)))
                return true;

            // `uniq in out` writes `out`: only the filter form (no file operands) is read-only.
            if (Regex.IsMatch(segment, @"\Auniq\b") && segment.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Count(a => !a.StartsWith('-')) > 1)
                return true;
        }

        return false;
    }

    /// <summary>A whole segment that starts with <paramref name="head"/> followed by arguments.</summary>
    private static Regex Segment(string head) =>
        new(@"\A" + head + @"(\s+[^|]*)?\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
