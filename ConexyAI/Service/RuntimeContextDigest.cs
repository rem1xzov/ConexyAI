namespace ConexyAI.Service;

/// <summary>
/// RUNTIME_CONTEXT: snapshot of the run state goes into the context only when it changes.
/// Ported from deepseek-harness (context/agent-instructions/src/runtime-context.ts:141-158).
/// </summary>
public sealed class RuntimeContextDigest
{
    /// <summary>Reset text, verbatim from the harness.</summary>
    public const string ResetText =
        "Current runtime context: none. Earlier runtime-context snapshots no longer apply.";

    private string? _last;

    /// <summary>What to append: null - nothing changed.</summary>
    public string? Delta(IEnumerable<string>? snapshot)
    {
        var lines = (snapshot ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0)
        {
            if (_last is null)
                return null;
            _last = null;
            return ResetText;
        }

        var text = "Runtime context:" + Environment.NewLine +
            string.Join(Environment.NewLine, lines.Select(l => "- " + l.Trim()));
        if (text == _last)
            return null;
        _last = text;
        return text;
    }
}
