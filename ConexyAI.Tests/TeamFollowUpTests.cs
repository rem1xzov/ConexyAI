using System.Runtime.CompilerServices;
using ConexyAI.Service;

// TEAM_FOLLOW_UP: добавлено 2026-11-20
/// <summary>
/// Продолжение помощника: новый запрос + его прошлый отчёт + запрет повторять сделанное.
/// Логика чистая — проверяется без прогона модели.
/// </summary>
internal static class TeamFollowUpTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("team follow-up: the brief carries the new task, the previous report and the no-repeat rule", BriefAsync);
        TestRegistry.Add("team follow-up: a huge previous report is truncated", TruncateAsync);
        TestRegistry.Add("team follow-up: an empty follow-up produces no brief", EmptyAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task BriefAsync()
    {
        var brief = TeamFollowUp.BuildBrief("API готов, тесты зелёные", "Теперь проверь UI на 320px");
        Assert(brief.Contains("Теперь проверь UI на 320px", StringComparison.Ordinal), "the new task is present");
        Assert(brief.Contains("API готов", StringComparison.Ordinal), "the previous report is present");
        Assert(brief.Contains("Не повторяй", StringComparison.Ordinal), "the no-repeat instruction is present");

        var fresh = TeamFollowUp.BuildBrief(null, "Сделай часть B");
        Assert(fresh.Contains("Прошлого отчёта нет", StringComparison.Ordinal), "a helper without a report gets a clear note");
        return Task.CompletedTask;
    }

    private static Task TruncateAsync()
    {
        var brief = TeamFollowUp.BuildBrief(new string('x', 10_000), "Продолжай");
        Assert(brief.Length < 10_000, "the previous report is truncated before it reaches the model");
        Assert(brief.Contains("…", StringComparison.Ordinal), "truncation is marked");
        return Task.CompletedTask;
    }

    private static Task EmptyAsync()
    {
        Assert(TeamFollowUp.BuildBrief("было", "   ") == string.Empty, "an empty follow-up has no brief");
        Assert(TeamFollowUp.BuildBrief("было", null) == string.Empty, "null follow-up has no brief");
        return Task.CompletedTask;
    }
}
