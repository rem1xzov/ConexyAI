using ConexyAI.Contract;

namespace ConexyAI.Service;

// SUBAGENT_AUTONOMY: добавлено 2026-11-19 — политика самостоятельного запуска помощников. Вынесена
// отдельно от раннера, чтобы её проверяли юнит-тестами: раньше всё решал один клиентский флаг /team,
// и при выключенном переключателе модель даже не видела `spawn_agents`.
public static class SubagentPolicy
{
    /// <summary>Whether the lead of this run may start helpers on its own.</summary>
    public static bool SpawnAllowed(ConexyJob job, string profileMode) =>
        (job.Orchestra || job.CanSpawnAgents) && profileMode == "coder";

    /// <summary>Whether the run may hand tasks inside the team (<c>team_message</c>).</summary>
    public static bool TeamMessageAllowed(ConexyJob job, string profileMode) =>
        (job.Orchestra || job.CanSpawnAgents) && profileMode is "coder" or "worker";

    /// <summary>Whether the run may inspect the team (<c>agents_status</c>).</summary>
    public static bool AgentsStatusAllowed(ConexyJob job, string profileMode) =>
        (job.Orchestra || job.CanSpawnAgents) && profileMode == "coder";
}
