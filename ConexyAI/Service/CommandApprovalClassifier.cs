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

    // Read-only commands that are safe to run without asking. Anchored at the start of a pipeline
    // segment, so `cat x | grep y` is safe while `cat x > y` is not.
    private static readonly Regex[] SafeSegmentPatterns =
    {
        // Listing / inspecting / reading.
        new(@"^(ls|dir|tree|pwd|cat|head|tail|wc|file|stat|du|df|realpath|basename|dirname|nl|column|readlink)\b", RegexOptions.Compiled),
        // Searching.
        new(@"^(grep|rg|ag|fd|find)\b", RegexOptions.Compiled),
        // Text processing that only reads (sed without -i never writes).
        new(@"^(sort|uniq|cut|tr|jq|diff|comm|strings)\b", RegexOptions.Compiled),
        new(@"^sed\s+-n\b", RegexOptions.Compiled),
        // Environment / metadata.
        new(@"^(which|where|whoami|id|hostname|date|uname|env|printenv|type|command\s+-v)\b", RegexOptions.Compiled),
        // Git porcelain commands that never change the repository.
        new(@"^git\s+(status|log|diff|show|branch|remote|rev-parse|ls-files|blame|describe|shortlog|whatchanged)\b", RegexOptions.Compiled),
        new(@"^git\s+stash\s+list\b", RegexOptions.Compiled),
        new(@"^git\s+config\s+--get\b", RegexOptions.Compiled),
        new(@"^(git|dotnet|npm|pnpm|yarn|node|python3?|go|java|rustc|cargo|tsc|pip)\s+(--version|-v|-V|version)\b", RegexOptions.Compiled),
        new(@"^dotnet\s+(--info|--list-sdks|--list-runtimes)\b", RegexOptions.Compiled),
        new(@"^(npm|pnpm|yarn)\s+(ls|list|view|outdated)\b", RegexOptions.Compiled),
        new(@"^echo\b", RegexOptions.Compiled),
    };

    // Anything that can write, delete, network-fetch or change permissions is not "read-only",
    // even when it is not violent enough for the dangerous list.
    private static readonly Regex MutatingPattern =
        new(
            @"\b(rm|rmdir|mv|cp|touch|mkdir|ln|tee|dd|truncate|chmod|chown|chgrp|install|apt|apt-get|yum|brew|pip|pip3|curl|wget|git\s+(add|commit|push|pull|fetch|checkout|switch|reset|clean|merge|rebase|stash|tag|init|clone)|npm\s+(i|install|ci|uninstall|update)|pnpm\s+(i|install|add|remove)|yarn\s+(add|install|remove)|dotnet\s+(add|restore|nuget|publish|run|build|test)|cargo\s+(build|test|run|add)|docker|kubectl|systemctl|service|sudo|sed\s+-i)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Shell constructs that can hide a second, mutating command behind a "safe" first one.
    private static readonly Regex ChainingPattern =
        new(@"(>>|>|<<|;|&&|\|\||\$\(|`)", RegexOptions.Compiled);

    public bool RequiresApproval(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return true;

        // Destructive patterns always win, whatever the safe list thinks.
        if (_dangerous.IsDangerous(command))
            return true;

        // A redirect, a command substitution or a chain can smuggle a write into an otherwise
        // harmless command, so those never qualify as read-only.
        if (ChainingPattern.IsMatch(command))
            return true;

        if (MutatingPattern.IsMatch(command))
            return true;

        // Every segment of the pipeline must itself be a known read-only command.
        var segments = command.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return true;

        foreach (var raw in segments)
        {
            var segment = raw.Trim();
            if (segment.Length == 0)
                continue;

            if (!SafeSegmentPatterns.Any(p => p.IsMatch(segment)))
                return true;
        }

        return false;
    }
}
