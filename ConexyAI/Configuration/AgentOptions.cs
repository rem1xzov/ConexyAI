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
}
