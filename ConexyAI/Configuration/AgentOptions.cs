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
    /// LOOP_GUARD: with the loop guard, the web budget and the token breaker in place there is no
    /// reason for a large number here — 25 covers complex tasks, and everything above it used to be a
    /// runaway loop burning quota. Must be &gt;= 1; a value of &lt;= 0 falls back to 25.
    /// ITERATION_WRAPUP / TOKEN_ECONOMY: с 2026-10-06 значение снижено до 15. Каждый шаг пересылает
    /// всю переписку заново, поэтому 25 шагов — это лишние пересылки на длинных задачах. На последнем
    /// шаге агенту добавляется распоряжение подвести итог (PromptFragments.FinalStepWrapUp), поэтому
    /// работа не теряется: прогон заканчивается ответом, а не служебной строкой. Продолжить всегда
    /// можно новым сообщением в том же чате.
    /// </summary>
    public int MaxIterations { get; set; } = 15;

    /// <summary>
    /// AUDITOR_BUDGET: добавлено 2026-09-23. Hard upper bound, in seconds, for one Maker-Checker
    /// audit (reading the changed files plus the reviewer's model call and its retries). The audit
    /// runs AFTER the final answer has already been streamed to the chat, so every second it takes is
    /// a second the user sees a finished answer on a task that is still "running". When the bound is
    /// hit the answer is finalized without review instead of failing the task.
    /// AUDIT_HANG: снижено с 90 до 45 (2026-10-10) — 90-секундная пауза после готового ответа
    /// выглядела как зависание. Значение можно менять без пересборки (Agent__AuditTimeoutSeconds).
    /// A value of &lt;= 0 falls back to the default (45).
    /// </summary>
    public int AuditTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// AUDIT_HANG: добавлено 2026-10-10 — выключатель Maker-Checker. Аудит — это отдельный большой
    /// вызов модели ПОСЛЕ уже доставленного ответа: он улучшает качество, но добавляет молчаливую
    /// паузу (и повторные круги при REJECT). Можно выключить без пересборки — Agent__AuditEnabled=false.
    /// </summary>
    public bool AuditEnabled { get; set; } = true;

    /// <summary>
    /// SELF_CORRECTION: добавлено 2026-09-24. How many times the agent is sent back when it tries to
    /// finish while a build/test/lint command it ran is still failing ("red"). Every push-back quotes
    /// the tail of the failing output. After the budget the run finishes with the model's answer plus
    /// an explicit note of what is still failing — it is never failed or discarded for that.
    /// A value &lt;= 0 falls back to the default (5); values above 20 are capped.
    /// </summary>
    public int MaxSelfCorrectionAttempts { get; set; } = 5;
}
