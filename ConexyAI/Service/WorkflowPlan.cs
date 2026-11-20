using System.Text.Json;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// WORKFLOW: добавлено 2026-11-20 (идея DeepSeek Harness, packages/workflow) — структурированный фан-аут
// команды: не просто «2–4 роли параллельно», а план задач с зависимостями. Задачи идут ВОЛНАМИ: в
// одной волне они независимы и выполняются параллельно, а следующая волна получает отчёты предыдущих
// в задании. Это даёт «сначала backend и frontend, потом QA после обоих» без ручных `after` в промпте.
public sealed record WorkflowStep(
    string Id,
    string Title,
    string Brief,
    IReadOnlyList<string> After,
    IReadOnlyList<string> Scope,
    WorkerPreset Preset = WorkerPreset.Default);

public sealed class WorkflowPlan
{
    /// <summary>Сколько задач в одном плане (те же бюджеты, что у команды).</summary>
    public const int MaxSteps = 4;
    public const int MinSteps = 2;
    private const int MaxIdChars = 40;
    private const int MaxNotesChars = 2_000;

    private WorkflowPlan(IReadOnlyList<WorkflowStep> steps) => Steps = steps;

    public IReadOnlyList<WorkflowStep> Steps { get; }

    /// <summary>Разбирает `tasks` из аргументов инструмента; <c>null</c> + ошибка, если план невалиден.</summary>
    public static WorkflowPlan? TryParse(JsonElement tasksElement, out string? error)
    {
        error = null;
        if (tasksElement.ValueKind != JsonValueKind.Array)
        {
            error = "workflow_run требует 'tasks' — список задач (2–4).";
            return null;
        }

        var steps = new List<WorkflowStep>();
        foreach (var item in tasksElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "каждая задача — объект {id, title, brief, scope, after?}.";
                return null;
            }

            var id = Text(item, "id")?.Trim() ?? string.Empty;
            var title = Text(item, "title")?.Trim() ?? string.Empty;
            var brief = Text(item, "brief")?.Trim() ?? string.Empty;
            if (id.Length == 0 || id.Length > MaxIdChars)
            {
                error = "у задачи должен быть короткий id (до 40 символов).";
                return null;
            }
            if (brief.Length == 0)
            {
                error = $"у задачи '{id}' пустое задание (brief).";
                return null;
            }

            steps.Add(new WorkflowStep(
                id,
                title.Length == 0 ? id : title,
                brief,
                TextList(item, "after"),
                TextList(item, "scope", normalizePaths: true),
                WorkerPresets.Parse(Text(item, "preset"))));
        }

        var plan = new WorkflowPlan(steps);
        var problems = plan.Validate();
        if (problems.Count > 0)
        {
            error = string.Join(" ", problems);
            return null;
        }

        return plan;
    }

    /// <summary>Ошибки плана: размер, дубли id, неизвестные зависимости, циклы.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Steps.Count is < MinSteps or > MaxSteps)
            problems.Add($"в плане должно быть от {MinSteps} до {MaxSteps} задач, сейчас {Steps.Count}");
        if (Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != Steps.Count)
            problems.Add("есть дубликаты id задач");
        problems.AddRange(TeamTaskBoard.Validate(ToTasks()).Where(problem => !problem.Contains("владелец", StringComparison.Ordinal)));
        return problems;
    }

    /// <summary>Пересечения областей записи (предупреждение: агенты всё ещё могут писать в один путь).</summary>
    public IReadOnlyList<string> ScopeOverlaps() => TeamTaskBoard.ScopeOverlaps(ToTasks());

    /// <summary>Волны: в каждой задачи независимы и могут идти параллельно.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Waves() => TeamTaskBoard.ExecutionWaves(ToTasks());

    /// <summary>Задание задачи с отчётами уже завершённых зависимостей.</summary>
    public static string BuildStepBrief(WorkflowStep step, IReadOnlyDictionary<string, string> reports)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(step.Title).Append(": ").Append(step.Brief);
        var notes = step.After
            .Where(reports.ContainsKey)
            .Select(id => $"[{id}] {Truncate(reports[id], MaxNotesChars)}")
            .ToList();
        if (notes.Count > 0)
            builder.Append("\n\nОтчёты задач, которые уже выполнены:\n").Append(string.Join("\n\n", notes));
        return builder.ToString();
    }

    public IReadOnlyList<TeamTask> ToTasks() =>
        Steps.Select(step => new TeamTask(
            step.Id,
            step.Title,
            TeamTaskStatus.Pending,
            DependsOn: step.After,
            WriteScopes: step.Scope)).ToList();

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> TextList(JsonElement item, string name, bool normalizePaths = false)
    {
        var values = new List<string>();
        if (!item.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return values;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                continue;
            var value = entry.GetString()?.Trim() ?? string.Empty;
            if (normalizePaths)
                value = value.Replace('\\', '/').Trim('/');
            if (value.Length > 0)
                values.Add(value);
        }

        return values;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
