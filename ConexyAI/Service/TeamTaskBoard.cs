namespace ConexyAI.Service;

// TEAM_BOARD: добавлено 2026-11-20 (идея DeepSeek Harness, packages/team/agent-team task-graph) —
// доска задач команды вместо статического списка ролей: у задач есть зависимости, владелец, области
// записи и ревизия для конкурентных обновлений (CAS). Логика чистая, поэтому проверяется тестами.
public enum TeamTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Blocked,
}

/// <summary>Одна задача доски: что сделать, кто владеет, что должно завершиться раньше и какие пути можно менять.</summary>
public sealed record TeamTask(
    string Id,
    string Title,
    TeamTaskStatus Status = TeamTaskStatus.Pending,
    string? Owner = null,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<string>? WriteScopes = null,
    int Revision = 0);

public static class TeamTaskBoard
{
    /// <summary>Сколько задач держит доска одного прогона.</summary>
    public const int MaxTasks = 50;

    /// <summary>Ошибки структуры доски: дубликаты, неизвестные зависимости, циклы, пустые поля, переполнение.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<TeamTask> tasks)
    {
        var problems = new List<string>();
        if (tasks.Count > MaxTasks)
            problems.Add($"на доске больше {MaxTasks} задач");
        var ids = tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != tasks.Count)
            problems.Add("есть дубликаты id задач");

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Title))
                problems.Add("у задачи должны быть id и название");
            foreach (var dependency in task.DependsOn ?? [])
            {
                if (!ids.Contains(dependency))
                    problems.Add($"{task.Id}: зависимость '{dependency}' не существует");
                else if (dependency == task.Id)
                    problems.Add($"{task.Id}: задача не может зависеть от себя");
            }
            if (task.Status != TeamTaskStatus.Pending && string.IsNullOrWhiteSpace(task.Owner))
                problems.Add($"{task.Id}: у задачи в работе должен быть владелец");
        }

        // Циклы ищем только когда id уникальны: иначе словарь падает вместо понятной ошибки о дубликатах.
        if (ids.Count == tasks.Count)
            problems.AddRange(FindCycle(tasks).Select(cycle => "цикл зависимостей: " + string.Join(" -> ", cycle)));
        return problems;
    }

    /// <summary>Пересечения областей записи: два агента не должны править один путь.</summary>
    public static IReadOnlyList<string> ScopeOverlaps(IReadOnlyList<TeamTask> tasks)
    {
        var overlaps = new List<string>();
        for (var i = 0; i < tasks.Count; i++)
        for (var j = i + 1; j < tasks.Count; j++)
        foreach (var a in tasks[i].WriteScopes ?? [])
        foreach (var b in tasks[j].WriteScopes ?? [])
        {
            if (a.Equals(b, StringComparison.Ordinal) || a.StartsWith(b + "/", StringComparison.Ordinal)
                || b.StartsWith(a + "/", StringComparison.Ordinal))
            {
                overlaps.Add($"{tasks[i].Id}:{a} ↔ {tasks[j].Id}:{b}");
            }
        }

        return overlaps;
    }

    /// <summary>
    /// Взять задачу в работу, если она свободна, готова (все зависимости завершены) и ревизия совпала.
    /// Возвращает обновлённую задачу или <c>null</c>, когда брать нельзя.
    /// </summary>
    public static TeamTask? Claim(IReadOnlyList<TeamTask> tasks, string id, string owner, int expectedRevision)
    {
        var task = tasks.FirstOrDefault(item => item.Id == id);
        if (task is null || task.Revision != expectedRevision || task.Status != TeamTaskStatus.Pending || string.IsNullOrWhiteSpace(owner))
            return null;
        var dependencies = task.DependsOn ?? [];
        if (dependencies.Any(dependency => tasks.FirstOrDefault(item => item.Id == dependency)?.Status != TeamTaskStatus.Completed))
            return null;
        return task with { Status = TeamTaskStatus.InProgress, Owner = owner.Trim(), Revision = task.Revision + 1 };
    }

    /// <summary>Закрыть свою задачу той же ревизией, что была при взятии.</summary>
    public static TeamTask? Complete(IReadOnlyList<TeamTask> tasks, string id, string owner, int expectedRevision)
    {
        var task = tasks.FirstOrDefault(item => item.Id == id);
        if (task is null || task.Revision != expectedRevision || task.Status != TeamTaskStatus.InProgress
            || !string.Equals(task.Owner, owner?.Trim(), StringComparison.Ordinal))
            return null;
        return task with { Status = TeamTaskStatus.Completed, Revision = task.Revision + 1 };
    }

    /// <summary>
    /// Волны выполнения: в каждой волне задачи независимы и могут идти параллельно, все зависимости
    /// волны уже завершены предыдущими волнами. Пустой список — цикл (планировать нельзя).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ExecutionWaves(IReadOnlyList<TeamTask> tasks)
    {
        var pending = tasks.ToDictionary(
            task => task.Id,
            task => new HashSet<string>(task.DependsOn ?? [], StringComparer.Ordinal),
            StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var waves = new List<IReadOnlyList<string>>();

        while (pending.Count > 0)
        {
            var wave = pending
                .Where(pair => pair.Value.All(dependency => done.Contains(dependency)))
                .Select(pair => pair.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            if (wave.Count == 0)
                break;
            waves.Add(wave);
            foreach (var id in wave)
            {
                done.Add(id);
                pending.Remove(id);
            }
        }

        return waves;
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindCycle(IReadOnlyList<TeamTask> tasks)
    {
        var cycles = new List<IReadOnlyList<string>>();
        var byId = tasks.GroupBy(task => task.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
            {
                var start = path.IndexOf(id);
                cycles.Add(path.Skip(start < 0 ? 0 : start).Append(id).ToList());
                return;
            }
            path.Add(id);
            foreach (var dependency in byId[id].DependsOn ?? [])
            {
                if (byId.ContainsKey(dependency)) Visit(dependency);
            }
            path.RemoveAt(path.Count - 1);
            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var task in tasks) Visit(task.Id);
        return cycles;
    }
}
