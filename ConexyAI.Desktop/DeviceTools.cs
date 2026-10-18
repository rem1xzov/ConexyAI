using System.Collections.Concurrent;
using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>
/// Инструменты устройства для задач с сайта: список проектов, ToolRunner на каждый корень (кэш), маршрутизация
/// подтверждений. Вызовы с сайта всегда идут через отдельные ToolRunner — режим плана и /rewind локального
/// сеанса их не касаются.
/// </summary>
public sealed class DeviceTools
{
    private readonly ConcurrentDictionary<string, ToolRunner> _runners =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task<string?>>? _gitHubToken;
    private readonly Mcp.LocalTools? _local;
    private readonly string? _interactiveRoot;

    public DeviceTools(bool daemon, ICommandApprover? console, Action<string> log,
        Func<CancellationToken, Task<string?>>? gitHubToken = null, Mcp.LocalTools? local = null, string? interactiveRoot = null)
    {
        Daemon = daemon;
        Remote = new RemoteApprover();
        Approver = new RoutingApprover(daemon ? null : console, Remote);
        Log = log;
        _gitHubToken = gitHubToken;
        _local = local;
        _interactiveRoot = interactiveRoot;
        Projects = ProjectStore.Load();
    }

    /// <summary>Фоновый режим: консоли нет, все подтверждения — на сайте.</summary>
    public bool Daemon { get; }

    public RemoteApprover Remote { get; }

    public ICommandApprover Approver { get; }

    public Action<string> Log { get; }

    public ProjectStore Projects { get; private set; }

    /// <summary>Перечитать projects.json (демон подхватывает conexy projects add без перезапуска).</summary>
    public void ReloadProjects() => Projects = ProjectStore.Load();

    /// <summary>Корень по умолчанию: --dir интерактивного сеанса или первый проект демона.</summary>
    public string? DefaultRoot => _interactiveRoot ?? Projects.Projects.FirstOrDefault()?.Path;

    public string? RootOf(string projectId) => Projects.Projects
        .FirstOrDefault(p => p.Id.Equals(projectId, StringComparison.OrdinalIgnoreCase))?.Path;

    public ToolRunner RunnerFor(string root) =>
        _runners.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            r => new ToolRunner(r, Approver, Log, _gitHubToken, _local));
}
