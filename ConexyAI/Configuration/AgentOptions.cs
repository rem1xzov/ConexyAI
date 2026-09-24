namespace ConexyAI.Configuration;

/// <summary>
/// Binds the <c>Agent</c> section of appsettings.json. Controls the autonomous
/// <c>conexy-coder</c> agent loop.
/// </summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Maximum number of autonomous iterations (LLM round-trips) the agent may perform
    /// for a single task before giving up. This is also the safety bound that prevents a
    /// runaway agent loop from hanging the worker or burning API tokens indefinitely.
    /// Must be &gt;= 1; a value of &lt;= 0 falls back to the default (15).
    /// </summary>
    public int MaxIterations { get; set; } = 15;

    /// <summary>
    /// AUDITOR_BUDGET: добавлено 2026-09-23. Hard upper bound, in seconds, for one Maker-Checker
    /// audit (reading the changed files plus the reviewer's model call and its retries). The audit
    /// runs AFTER the final answer has already been streamed to the chat, so every second it takes is
    /// a second the user sees a finished answer on a task that is still "running". When the bound is
    /// hit the answer is finalized without review instead of failing the task.
    /// A value of &lt;= 0 falls back to the default (90).
    /// </summary>
    public int AuditTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// SELF_CORRECTION: добавлено 2026-09-24. How many times the agent is sent back when it tries to
    /// finish while a build/test/lint command it ran is still failing ("red"). Every push-back quotes
    /// the tail of the failing output. After the budget the run finishes with the model's answer plus
    /// an explicit note of what is still failing — it is never failed or discarded for that.
    /// A value &lt;= 0 falls back to the default (5); values above 20 are capped.
    /// </summary>
    public int MaxSelfCorrectionAttempts { get; set; } = 5;
}
