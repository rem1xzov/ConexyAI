namespace ConexyAI.Service;

// TURN_OUTCOME: добавлено 2026-11-20 (идея DeepSeek Harness, TurnEndReasonMap) — причина завершения
// прогона теперь идёт вместе с короткой заметкой и машинным кодом для логов/событий. Сам набор причин
// живёт в давно существующем enum TurnOutcome (ConversationService) — дубликат не нужен.
public sealed record TurnResult(TurnOutcome Reason, string? Note = null)
{
    /// <summary>completed | forced_final | token_budget | stopped | failed — для событий и логов.</summary>
    public string Code => Reason switch
    {
        TurnOutcome.Completed => "completed",
        TurnOutcome.ForcedFinal => "forced_final",
        TurnOutcome.TokenBudget => "token_budget",
        TurnOutcome.Stopped => "stopped",
        _ => "failed",
    };
}
