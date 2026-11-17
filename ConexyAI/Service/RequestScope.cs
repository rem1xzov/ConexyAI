using System.Text.RegularExpressions;

namespace ConexyAI.Service;

/// <summary>Что пользователь попросил сделать в этом запросе.</summary>
public enum ScopeIntent
{
    /// <summary>Запустить/открыть/показать уже готовое. Правки не просили.</summary>
    Operate,

    /// <summary>Изменить код: починить, добавить, отрефакторить, реализовать.</summary>
    Change,

    /// <summary>Объяснить, разобрать, найти причину — без правок.</summary>
    Explain,

    /// <summary>Непонятно — ведём себя как раньше (правки разрешены).</summary>
    Unknown,
}

/// <summary>
/// SCOPE: рамка запроса. В deepseek-harness на сессию держится одна цель (<c>packages/goal</c>), и она
/// решает, когда работа закончена; у нас такой цели не было, поэтому «запусти проект» превращалось в
/// рефакторинг проекта. Здесь цель выводится из самой просьбы и ограничивает то, что агенту разрешено.
/// </summary>
public sealed partial record RequestScope(ScopeIntent Intent, string Objective)
{
    /// <summary>Правки файлов допустимы (изменение кода или непонятный запрос).</summary>
    public bool MutationsAllowed => Intent is ScopeIntent.Change or ScopeIntent.Unknown;

    /// <summary>Запрос — только действие/демонстрация: закончить, как только результат получен.</summary>
    public bool FinishOnOutcome => Intent == ScopeIntent.Operate;

    /// <summary>Отказ на правку файла, когда запрос был только «запусти/покажи».</summary>
    public string MutationRefusal =>
        "[Scope] Пользователь просил только: «" + Shorten(Objective) + "». Это не запрос на изменение кода, " +
        "поэтому правки файлов не выполняются. Доведи запуск/показ и дай короткий итог; если без правки " +
        "не обойтись — назови, что именно надо изменить, и спроси разрешение.";

    /// <summary>Строка цели для системного сообщения каждого хода.</summary>
    public string Prompt =>
        "[Scope] Цель запроса: «" + Shorten(Objective) + "». Делай ровно это. " +
        (Intent == ScopeIntent.Operate
            ? "Запрос операционный: запусти/открой/покажи и остановись, как только результат получен. " +
              "Не рефактори, не добавляй фичи, не чини попутно найденное — только сообщи о нём."
            : Intent == ScopeIntent.Explain
                ? "Запрос на разбор: исследуй и ответь, ничего не меняя."
                : "Меняй только то, что нужно для этой цели, и не расширяй задачу.");

    /// <summary>Разбирает просьбу пользователя. При неоднозначности возвращает <see cref="ScopeIntent.Unknown"/>.</summary>
    public static RequestScope From(string? request)
    {
        var text = (request ?? string.Empty).Trim();
        if (text.Length == 0)
            return new RequestScope(ScopeIntent.Unknown, text);

        var lower = text.ToLowerInvariant();

        // NEGATION: «не запускай», «не надо открывать» — это не операционный запрос.
        var operate = Matches(OperateVerbs, lower) && !NegatedOperate.IsMatch(lower);
        var change = Matches(ChangeVerbs, lower);
        var explain = Matches(ExplainVerbs, lower);

        // CHANGE побеждает: «запусти и почини» — это работа с кодом.
        var intent = change ? ScopeIntent.Change
            : operate ? ScopeIntent.Operate
            : explain ? ScopeIntent.Explain
            : ScopeIntent.Unknown;

        return new RequestScope(intent, text);
    }

    private static bool Matches(Regex regex, string text) => regex.IsMatch(text);

    private static string Shorten(string text) =>
        text.Length <= 160 ? text : text[..157] + "…";

    // Глаголы по смыслу, а не один сценарий: операционные — запуск/показ готового.
    private static readonly Regex OperateVerbs = Compiled(
        @"\b(запусти|запустить|запускай|подними|поднять|поднимай|стартани|открой|открыть|покажи|показать|"
        + @"прогони|прогнать|демо|посмотреть|глянь|включи|разверни)\b"
        + @"|\b(run|start|launch|open|show|serve|demo|preview|host)\b");

    private static readonly Regex ChangeVerbs = Compiled(
        @"\b(почини|починить|исправь|исправить|фикс|добавь|добавить|реализуй|реализовать|сделай|сделать|"
        + @"перепиши|переписать|отрефактори|рефакторинг|обнови|обновить|удали|удалить|переименуй|внедри|"
        + @"доведи|доделай|доделать|напиши|написать|создай|создать)\b"
        + @"|\b(fix|add|implement|refactor|rewrite|update|remove|delete|rename|create|build|make)\b");

    private static readonly Regex ExplainVerbs = Compiled(
        @"\b(объясни|объяснить|разбери|разобрать|почему|зачем|где|как работает|проанализируй|анализ|найди|найти|"
        + @"расскажи|опиши|проверь)\b"
        + @"|\b(explain|why|analyze|analyse|find|review|check|describe)\b");

    // «не запускай», «не надо открывать», «don't run» — просьба НЕ выполнять действие.
    private static readonly Regex NegatedOperate = Compiled(
        @"\bне\s+(надо\s+)?(запуска|запусти|открыва|открыть|показыва|покажи|поднима|подними)"
        + @"|\b(don'?t|do not|no need to)\s+(run|start|launch|open|show)\b");

    private static Regex Compiled(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
