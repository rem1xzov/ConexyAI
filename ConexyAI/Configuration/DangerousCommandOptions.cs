namespace ConexyAI.Configuration;

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
/// <summary>
/// Binds the <c>DangerousCommandPatterns</c> section of appsettings.json. Each entry is a
/// .NET regex matched (case-insensitively) against the raw <c>bash</c> tool command. A
/// command that matches any pattern is paused and requires explicit user confirmation
/// before it is executed. The list is config-only so new rules can be added without
/// rebuilding the backend.
/// </summary>
public class DangerousCommandOptions
{
    public const string SectionName = "DangerousCommandPatterns";

    /// <summary>Regex patterns (case-insensitive) that classify a command as dangerous.</summary>
    public List<string> Patterns { get; set; } = new();
}
