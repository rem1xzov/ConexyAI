using System.Security.Cryptography;
using System.Text;

namespace ConexyAI.Service;

// LOOP_GUARD: добавлено 2026-09-27
/// <summary>
/// Защита от зацикливания внутри ОДНОГО запуска агента.
/// <para>
/// Держит скользящее окно последних вызовов инструментов и отказывает в повторе ДО того, как команда
/// уйдёт в терминал: если одна и та же команда уже три раза подряд вернула одно и то же, четвёртый
/// запуск ничего нового не даст, а токены и время съест. Отдельно считает обращения к интернету
/// (поиск + чтение страниц) — но только внутри ОДНОГО хода модели: счётчик сбрасывается на каждом
/// шаге цикла, поэтому за задачу поиск не ограничен, ограничен лишь «взрыв» поисков в одном ответе.
/// </para>
/// <para>
/// Экземпляр создаётся на прогон (в <c>RunLoopAsync</c>), а не живёт в сервисе: петля — свойство
/// конкретной задачи, и состояние не должно перетекать в следующий запуск.
/// </para>
/// </summary>
public sealed class AgentLoopGuard
{
    /// <summary>Сколько одинаковых вызовов с одинаковым результатом подряд считаются петлёй.</summary>
    public const int RepeatThreshold = 3;

    /// <summary>
    /// Сколько интернет-обращений (web_search + fetch_web_page) разрешено за ОДИН ход модели.
    /// Счётчик сбрасывается на каждом шаге цикла (<see cref="ResetWebBudget"/>), поэтому в целом за
    /// задачу поиск не ограничен — ограничен только «взрыв» поисков внутри одного ответа.
    /// </summary>
    public const int WebCallBudget = 10;

    /// <summary>Текст отказа при повторе. Модель читает его как результат инструмента.</summary>
    public const string RepeatRefusal =
        "Команда уже выполнялась несколько раз без изменения результата. Прекрати повторять одно и то же " +
        "действие. Проанализируй причину неудачи или заверши работу, объяснив пользователю проблему.";

    /// <summary>Текст отказа, когда интернет-бюджет этого хода исчерпан.</summary>
    public const string WebBudgetRefusal =
        "Лимит интернет-запросов на этот ход исчерпан. Не повторяй поиск сейчас — обработай уже " +
        "найденную информацию и продолжай задачу.";

    /// <summary>Инструменты, которые ходят в интернет и считаются в общий бюджет.</summary>
    private static readonly HashSet<string> WebTools = new(StringComparer.Ordinal)
    {
        "web_search",
        "fetch_web_page"
    };

    private readonly int _repeatThreshold;
    private readonly int _webCallBudget;
    private readonly int _windowSize;
    private readonly List<Call> _recent = new();

    public AgentLoopGuard(
        int repeatThreshold = RepeatThreshold,
        int webCallBudget = WebCallBudget,
        int windowSize = 8)
    {
        _repeatThreshold = Math.Max(1, repeatThreshold);
        _webCallBudget = Math.Max(0, webCallBudget);
        // Окно не может быть короче порога повторов, иначе петлю не из чего было бы собрать.
        _windowSize = Math.Max(_repeatThreshold, windowSize);
    }

    /// <summary>Сколько интернет-обращений уже израсходовано в этом ходе.</summary>
    public int WebCallsUsed { get; private set; }

    /// <summary>
    /// Открывает новый бюджет интернет-обращений — вызывается в начале каждого хода модели. За один
    /// ответ (ход) разрешено не больше <see cref="WebCallBudget"/> обращений, но на следующем шаге
    /// счётчик снова полон: за задачу поиск не ограничен, ограничен лишь взрыв поисков внутри хода.
    /// </summary>
    public void ResetWebBudget() => WebCallsUsed = 0;

    /// <summary>
    /// Можно ли выполнять вызов. <c>null</c> — можно; иначе причина отказа, которую надо вернуть
    /// модели вместо результата инструмента. Для интернет-инструментов бюджет списывается здесь же,
    /// поэтому метод вызывается РОВНО один раз на вызов инструмента.
    /// </summary>
    public string? Refuse(string tool, string arguments)
    {
        if (RefusesRepeat(tool, arguments))
        {
            return RepeatRefusal;
        }

        if (WebTools.Contains(tool))
        {
            if (WebCallsUsed >= _webCallBudget)
            {
                return WebBudgetRefusal;
            }

            WebCallsUsed++;
        }

        return null;
    }

    /// <summary>
    /// Запоминает фактический результат — только по нему и видно, что повтор ничего не меняет
    /// (упавшая команда и та же команда с новым выводом — разные ситуации).
    /// </summary>
    public void Record(string tool, string arguments, string output)
    {
        _recent.Add(new Call(tool, arguments, Hash(output)));

        if (_recent.Count > _windowSize)
        {
            _recent.RemoveRange(0, _recent.Count - _windowSize);
        }
    }

    /// <summary>
    /// Три (и больше) одинаковых вызова ПОДРЯД, и все они дали один и тот же вывод. Любой другой
    /// вызов между ними рвёт цепочку: «упало → поправил → снова упало» — это нормальная работа,
    /// а не петля.
    /// </summary>
    public bool RefusesRepeat(string tool, string arguments)
    {
        var matching = 0;
        string? output = null;

        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var call = _recent[i];
            if (!string.Equals(call.Tool, tool, StringComparison.Ordinal) ||
                !string.Equals(call.Arguments, arguments, StringComparison.Ordinal))
            {
                break;
            }

            output ??= call.OutputHash;
            if (!string.Equals(call.OutputHash, output, StringComparison.Ordinal))
            {
                // Вывод менялся от попытки к попытке — это попытки, а не петля.
                return false;
            }

            matching++;
        }

        return matching >= _repeatThreshold;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private readonly record struct Call(string Tool, string Arguments, string OutputHash);
}
