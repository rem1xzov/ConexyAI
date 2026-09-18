using System.Text.RegularExpressions;
using ConexyAI.Configuration;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
/// <summary>
/// Fast, pattern-based (regex) classifier for the <c>bash</c> tool. No LLM call is made:
/// the raw command string is matched against the configurable
/// <see cref="DangerousCommandOptions.Patterns"/> list so the agent loop can pause
/// deterministically before running a destructive/irreversible command.
/// </summary>
public interface IDangerousCommandClassifier
{
    /// <summary>Returns true when <paramref name="command"/> matches any configured dangerous pattern.</summary>
    bool IsDangerous(string command);
}

public class DangerousCommandClassifier : IDangerousCommandClassifier
{
    private readonly Regex[] _patterns;

    // Non-forced `rm -r` inside a clearly temporary location is treated as safe (the
    // requirement: "rm -r with paths outside temporary directories" is dangerous).
    private static readonly Regex RmRecursive = new(
        @"rm\s+-[a-z]*r(?!f)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TempPath = new(
        @"(/tmp|\\temp\\|(?<![a-z0-9])temp[/\\]|\$TMPDIR|mktemp|/var/tmp)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public DangerousCommandClassifier(IOptions<DangerousCommandOptions> options)
    {
        _patterns = Compile(options.Value.Patterns);
    }

    public bool IsDangerous(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(command))
            {
                // Allow a plain recursive remove inside an obvious temp dir.
                if (RmRecursive.IsMatch(command) && TempPath.IsMatch(command))
                    continue;

                return true;
            }
        }

        return false;
    }

    private static Regex[] Compile(IEnumerable<string> patterns)
    {
        var compiled = new List<Regex>();
        foreach (var raw in patterns ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            try
            {
                compiled.Add(new Regex(raw, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException)
            {
                // A bad config entry must never break the agent loop: skip it and keep going.
            }
        }

        return compiled.ToArray();
    }
}
