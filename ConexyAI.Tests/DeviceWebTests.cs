using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Model;
using ConexyAI.Service;
using ConexyAI.Service.Device;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// WEB_DEVICE: добавлено 2026-10-18 — запуск из веба «на моём ПК»: проекты устройства, маршрутизация
// вызовов (ToolProject/ToolRemote), очередь задач до подключения ПК и итоговый diff.
internal static class DeviceWebTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("web device: projects are sanitized, capped and bound to the device connection", ProjectsAsync);
        TestRegistry.Add("web device: ToolProject and ToolRemote precede ExecuteTool", RoutingAsync);
        TestRegistry.Add("web device: queued task keeps its reservation and is dispatched on connect", QueueDispatchAsync);
        TestRegistry.Add("web device: queued task can be moved to the sandbox only by its owner", QueueSandboxAsync);
        TestRegistry.Add("web device: turn diff is parsed and stored as a diff block", TurnDiffAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task ProjectsAsync()
    {
        var bridge = new DeviceToolBridge(new FakeHubContext(), NullLogger<DeviceToolBridge>.Instance);
        var user = Guid.NewGuid();

        Assert(!bridge.RegisterProjects(user, "c1", "[{\"id\":\"a\",\"name\":\"A\"}]"), "an unregistered connection must be refused");

        bridge.Register(user, "c1", "pc");
        var items = string.Join(",", Enumerable.Range(0, 80).Select(i => $"{{\"id\":\"p{i}\",\"name\":\"{new string('n', 150)}\"}}"));
        Assert(bridge.RegisterProjects(user, "c1", $"[{{\"id\":\"x\",\"name\":\"X\\u0007\",\"path\":\"C:\\\\x\"}},{{\"id\":\"x\"}},{{\"name\":\"no id\"}},{items}]"), "device connection accepted");
        var projects = bridge.GetInfo(user)!.Projects!;
        Assert(projects.Count == DeviceToolBridge.MaxProjects, $"capped to 50, got {projects.Count}");
        Assert(projects[0] is { Id: "x", Name: "X", Path: @"C:\x" }, "control chars stripped, path kept");
        Assert(projects.Count(p => p.Id == "x") == 1, "duplicate ids dropped");
        Assert(projects.All(p => p.Name.Length <= 100), "names capped to 100 chars");

        Assert(!bridge.RegisterProjects(user, "other", "[]"), "another connection cannot overwrite projects");
        bridge.Register(user, "c1", "pc");
        Assert(bridge.GetInfo(user)!.Projects!.Count == 50, "re-registering the same connection keeps projects");
        bridge.Register(user, "c2", "pc");
        Assert(bridge.GetInfo(user)!.Projects!.Count == 0, "a new connection starts without projects");
        Assert(DeviceToolBridge.ParseProjects("not json").Count == 0, "broken json is an empty list");
        return Task.CompletedTask;
    }

    private static async Task RoutingAsync()
    {
        var hub = new RecordingHubContext();
        var bridge = new DeviceToolBridge(hub, NullLogger<DeviceToolBridge>.Instance);
        var user = Guid.NewGuid();
        bridge.Register(user, "c1", "pc");

        var call = bridge.ExecuteAsync(user, Guid.NewGuid(), "bash", "{}", CancellationToken.None, "proj-1", remoteApprovals: true);
        for (var i = 0; i < 50 && !hub.Sent.Any(s => s.Method == "ExecuteTool"); i++)
            await Task.Delay(10);

        var sent = hub.Sent.ToArray();
        Assert(sent.Select(s => s.Method).SequenceEqual(new[] { "ToolProject", "ToolRemote", "ExecuteTool" }),
            $"order was {string.Join(",", sent.Select(s => s.Method))}");
        var callId = (Guid)sent[2].Args[0]!;
        Assert((Guid)sent[0].Args[0]! == callId && (string)sent[0].Args[1]! == "proj-1", "ToolProject(callId, projectId)");
        Assert((Guid)sent[1].Args[0]! == callId, "ToolRemote(callId)");
        Assert(bridge.Complete("c1", callId, "ok", false), "result accepted");
        Assert((await call).Output == "ok", "result returned");

        // Без проекта и без удалённых подтверждений — только ExecuteTool (как у CLI).
        while (hub.Sent.TryDequeue(out _)) { }
        var plain = bridge.ExecuteAsync(user, Guid.NewGuid(), "bash", "{}", CancellationToken.None);
        for (var i = 0; i < 50 && hub.Sent.IsEmpty; i++)
            await Task.Delay(10);
        var only = hub.Sent.ToArray();
        Assert(only.Length == 1 && only[0].Method == "ExecuteTool", "CLI calls are unchanged");
        bridge.Complete("c1", (Guid)only[0].Args[0]!, "", false);
        await plain;
    }

    private static (DeviceTaskQueue Queue, ConexyQueueGuard Guard, FakeQueue Inner) CreateQueue()
    {
        var inner = new FakeQueue();
        var guard = new ConexyQueueGuard(inner, NullLogger<ConexyQueueGuard>.Instance);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new DeviceTaskQueue(guard, scopes, new FakeHubContext(), NullLogger<DeviceTaskQueue>.Instance), guard, inner);
    }

    private static ConexyJob Job(Guid user, Guid task, Guid chat) =>
        new(task, chat, user, ConexyModelType.ConexyCoder, "prompt", UseDevice: true, DeviceProjectId: "p", RemoteApprovals: true);

    private static async Task QueueDispatchAsync()
    {
        var (queue, guard, inner) = CreateQueue();
        var user = Guid.NewGuid();
        var task = Guid.NewGuid();
        var chat = Guid.NewGuid();
        Assert(guard.TryReserve(task, chat), "reserved");
        queue.Hold(Job(user, task, chat));

        Assert(guard.IsInFlight(task), "reservation is kept while waiting");
        Assert(!guard.TryReserve(Guid.NewGuid(), chat), "the chat stays busy while waiting");
        Assert(queue.List(user).Count == 1 && queue.List(Guid.NewGuid()).Count == 0, "listed only for the owner");

        Assert(await queue.DispatchAsync(Guid.NewGuid()) == 0, "another user's device dispatches nothing");
        Assert(await queue.DispatchAsync(user) == 1, "dispatched on connect");
        Assert(inner.Enqueued.Count == 1 && inner.Enqueued[0].UseDevice && inner.Enqueued[0].DeviceProjectId == "p", "device job enqueued as-is");
        Assert(guard.IsInFlight(task), "still in flight until the worker finishes");
        Assert(!queue.Contains(task), "removed from the device queue");
    }

    private static async Task QueueSandboxAsync()
    {
        var (queue, guard, inner) = CreateQueue();
        var user = Guid.NewGuid();
        var task = Guid.NewGuid();
        guard.TryReserve(task, Guid.NewGuid());
        queue.Hold(Job(user, task, Guid.NewGuid()));

        Assert(!await queue.MoveToSandboxAsync(Guid.NewGuid(), task), "a stranger cannot move it");
        Assert(!await queue.CancelAsync(Guid.NewGuid(), task), "a stranger cannot cancel it");
        Assert(await queue.MoveToSandboxAsync(user, task), "the owner can move it");
        var job = inner.Enqueued.Single();
        Assert(!job.UseDevice && job.DeviceProjectId is null && !job.RemoteApprovals && job.DeviceTools is null, "runs in the sandbox");
        Assert(!await queue.MoveToSandboxAsync(user, task), "not queued anymore");
    }

    private static Task TurnDiffAsync()
    {
        var (files, truncated) = DeviceTurnDiff.Parse(
            "{\"files\":[{\"path\":\"a.txt\",\"status\":\"added\",\"diff\":\"+hi\\n\"},{\"path\":\"b\",\"status\":\"weird\",\"diff\":\"-x\"},{\"status\":\"deleted\"}],\"truncated\":true}");
        Assert(files.Count == 2 && truncated, "files without a path are dropped");
        Assert(files[1].Status == "modified", "unknown status normalized");

        var block = DeviceTurnDiff.BuildStoredBlock(files);
        Assert(block.Contains(DeviceTurnDiff.Heading) && block.Contains("```diff\n+hi\n-x\n```"), block);
        Assert(DeviceTurnDiff.BuildStoredBlock([]) == string.Empty, "no files — nothing appended");
        return Task.CompletedTask;
    }
}
