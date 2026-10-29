using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ConexyAI.Contract;
using ConexyAI.Controller;
using ConexyAI.DbContext;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Phone;
using ConexyAI.Service.Push;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// PHONE_AGENT: добавлено 2026-10-29 — мост телефона, подтверждения, каталог инструментов, REST-контроллер, push.
internal static class PhoneAgentTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("phone: registration is sanitized, online/foreground follow the connection", RegistrationAsync);
        TestRegistry.Add("phone: a result from another connection is rejected, the right one completes the call", ResultOwnershipAsync);
        TestRegistry.Add("phone: offline, dropped connection, bad arguments and timeout give clear errors", OfflineAsync);
        TestRegistry.Add("phone: WaitForOnline wakes on registration", WaitForOnlineAsync);
        TestRegistry.Add("phone approvals: approve / deny / timeout", ApprovalOutcomesAsync);
        TestRegistry.Add("phone approvals: decision is idempotent and owner-checked", ApprovalIdempotentAsync);
        TestRegistry.Add("phone approvals: confirm push only when the app is not on screen", ApprovalPushAsync);
        TestRegistry.Add("phone approvals: dismiss push when decided outside the notification or on timeout", DismissAsync);
        TestRegistry.Add("phone tools: every contract tool has a schema and the right R/C/D class", CatalogAsync);
        TestRegistry.Add("phone tools: arguments are validated and normalized against the schema", ArgumentsAsync);
        TestRegistry.Add("phone tools: image output is parsed and bounded", ImageOutputAsync);
        TestRegistry.Add("phone notifier: phone_needed after the wait, completed only in background", NotifierAsync);
        TestRegistry.Add("mobile: push tokens are deduplicated and capped per user", TokensAsync);
        TestRegistry.Add("mobile: action decision maps to 200/409/404 and validates input", ActionEndpointAsync);
        TestRegistry.Add("push: FCM assertion is a valid RS256 JWT, UNREGISTERED is detected", FcmHelpersAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static PhoneToolBridge NewBridge(RecordingHubContext? hub = null, Func<string, TimeSpan>? timeout = null) =>
        new(hub ?? new RecordingHubContext(), NullLogger<PhoneToolBridge>.Instance, timeout);

    private const string Info =
        """{"model":"Pixel 8","manufacturer":"Google","androidVersion":"15","sdk":35,"appVersion":"1.2.3","permissions":{"photos":"granted","camera":"denied","bogus":"granted","location":"hacked"}}""";

    private static Task RegistrationAsync()
    {
        var bridge = NewBridge();
        var user = Guid.NewGuid();
        Assert(!bridge.IsOnline(user) && bridge.GetInfo(user) is null, "no phone yet");

        Assert(bridge.Register(user, "c1", Info), "registered");
        var info = bridge.GetInfo(user)!;
        Assert(info is { Model: "Pixel 8", Manufacturer: "Google", AndroidVersion: "15", Sdk: 35 }, "info parsed");
        Assert(info.Permissions.Count == 2 && info.Permissions["photos"] == "granted" && info.Permissions["camera"] == "denied",
            "unknown permissions and states are dropped");
        Assert(bridge.IsOnline(user) && bridge.IsPhoneConnection(user, "c1"), "online after register");
        Assert(!bridge.IsPhoneConnection(Guid.NewGuid(), "c1"), "connection belongs to its user only");

        Assert(!bridge.IsForeground(user), "background by default");
        Assert(bridge.SetForeground(user, "c1", true) && bridge.IsForeground(user), "foreground set");
        Assert(!bridge.SetForeground(user, "other", false) && bridge.IsForeground(user), "foreign connection cannot change it");
        bridge.Register(user, "c1", Info);
        Assert(bridge.IsForeground(user), "re-registration keeps the foreground flag");

        bridge.Register(Guid.NewGuid(), "x", "not json");
        Assert(PhoneToolBridge.ParseInfo("not json").Model.Length == 0, "broken json — empty info");
        Assert(PhoneToolBridge.ParseInfo("{\"model\":\"A\\u0007B\"}").Model == "AB", "control chars stripped");

        bridge.Unregister("c1");
        Assert(!bridge.IsOnline(user) && !bridge.IsForeground(user), "offline after unregister, never foreground");
        return Task.CompletedTask;
    }

    private static async Task<(string CallId, string Tool, string Arguments)> WaitForCallAsync(RecordingHubContext hub)
    {
        for (var i = 0; i < 200; i++)
        {
            var sent = hub.Sent.FirstOrDefault(s => s.Method == "PhoneTool");
            if (sent.Method is not null)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(sent.Args[0]));
                var r = doc.RootElement;
                return (r.GetProperty("callId").GetString()!, r.GetProperty("tool").GetString()!, r.GetProperty("arguments").GetString()!);
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("PhoneTool event was not sent");
    }

    private static async Task ResultOwnershipAsync()
    {
        var hub = new RecordingHubContext();
        var bridge = NewBridge(hub);
        var user = Guid.NewGuid();
        bridge.Register(user, "phone-1", Info);

        var run = bridge.ExecuteAsync(user, Guid.NewGuid(), "phone_launch_app", "{\"package\":\"com.x\",\"evil\":1}", CancellationToken.None);
        var call = await WaitForCallAsync(hub);
        Assert(call.Tool == "phone_launch_app", "tool in the event");
        Assert(call.Arguments == "{\"package\":\"com.x\"}", $"only schema fields reach the phone, got {call.Arguments}");

        var callId = Guid.Parse(call.CallId);
        Assert(!bridge.Complete("someone-else", callId, "hacked", false), "a result from another connection is rejected");
        Assert(!run.IsCompleted, "the call is still waiting after the bad result");
        Assert(!bridge.Complete("phone-1", Guid.NewGuid(), "x", false), "an unknown callId is rejected");
        Assert(bridge.Complete("phone-1", callId, "ok", false), "the phone's own result is accepted");
        var outcome = await run;
        Assert(outcome is { Output: "ok", IsError: false }, "outcome returned");
        Assert(!bridge.Complete("phone-1", callId, "again", false), "a result cannot be delivered twice");
    }

    private static async Task OfflineAsync()
    {
        var bridge = NewBridge();
        var user = Guid.NewGuid();
        var offline = await bridge.ExecuteAsync(user, Guid.NewGuid(), "phone_device_info", "{}", CancellationToken.None);
        Assert(offline.IsError && offline.Output.StartsWith(PhoneToolBridge.OfflinePrefix, StringComparison.Ordinal), "offline error");

        var bad = await bridge.ExecuteAsync(user, Guid.NewGuid(), "phone_launch_app", "{}", CancellationToken.None);
        Assert(bad.IsError && bad.Output.Contains("package"), "missing required argument is reported before anything is sent");
        var unknown = await bridge.ExecuteAsync(user, Guid.NewGuid(), "phone_hack", "{}", CancellationToken.None);
        Assert(unknown.IsError, "unknown tool refused");

        var hub = new RecordingHubContext();
        var b2 = NewBridge(hub);
        b2.Register(user, "p", Info);
        var run = b2.ExecuteAsync(user, Guid.NewGuid(), "phone_battery_report", "{}", CancellationToken.None);
        await WaitForCallAsync(hub);
        b2.Unregister("p");
        var dropped = await run;
        Assert(dropped.IsError && dropped.Output.StartsWith(PhoneToolBridge.OfflinePrefix, StringComparison.Ordinal),
            "a dropped connection ends the pending call with the offline error");

        var b3 = NewBridge(new RecordingHubContext(), _ => TimeSpan.FromMilliseconds(150));
        b3.Register(user, "p", Info);
        var timedOut = await b3.ExecuteAsync(user, Guid.NewGuid(), "phone_battery_report", "{}", CancellationToken.None);
        Assert(timedOut.IsError && timedOut.Output.Contains("не ответил"), "timeout error");

        Assert(PhoneToolCatalog.TimeoutFor("phone_gallery_pick") == TimeSpan.FromMinutes(10)
               && PhoneToolCatalog.TimeoutFor("phone_request_permission") == TimeSpan.FromMinutes(10)
               && PhoneToolCatalog.TimeoutFor("phone_tap") == TimeSpan.FromMinutes(5), "per-tool timeouts");
    }

    private static async Task WaitForOnlineAsync()
    {
        var bridge = NewBridge();
        var user = Guid.NewGuid();
        Assert(!await bridge.WaitForOnlineAsync(user, TimeSpan.FromMilliseconds(50), CancellationToken.None), "times out when no phone");
        var wait = bridge.WaitForOnlineAsync(user, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        Assert(!wait.IsCompleted, "still waiting");
        bridge.Register(user, "c", Info);
        Assert(await wait, "registration wakes the waiter");
        Assert(await bridge.WaitForOnlineAsync(user, TimeSpan.Zero, CancellationToken.None), "online — immediately true");
    }

    // ---- approvals ----

    private sealed class FakePending : IPendingActionService
    {
        private sealed record Waiter(TaskCompletionSource<bool> Tcs, Guid UserId);
        private readonly ConcurrentDictionary<Guid, Waiter> _waiters = new();
        public List<(Guid ActionId, string Command, bool Dangerous, string? Description)> Created { get; } = new();

        public async Task<bool> WaitForDecisionAsync(Guid actionId, Guid userId, Guid chatId, Guid taskId, string command,
            string workingDirectory, bool isDangerous, CancellationToken ct, string? description = null)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[actionId] = new Waiter(tcs, userId);
            lock (Created)
                Created.Add((actionId, command, isDangerous, description));
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            try { return await tcs.Task; }
            finally { _waiters.TryRemove(actionId, out _); }
        }

        public Task<bool> ConfirmActionAsync(Guid actionId, Guid userId, bool approved, bool approveAll, CancellationToken ct)
        {
            if (_waiters.TryGetValue(actionId, out var w) && w.UserId == userId && _waiters.TryRemove(actionId, out _))
                return Task.FromResult(w.Tcs.TrySetResult(approved));
            return Task.FromResult(false);
        }

        public bool IsTaskAutoApproved(Guid taskId) => true; // телефон не должен обращаться к «разрешить всё»
        public void SetTaskAutoApproval(Guid taskId, bool enabled) { }

        public async Task<Guid> WaitForCreatedAsync(int count = 1)
        {
            for (var i = 0; i < 300; i++)
            {
                lock (Created)
                    if (Created.Count >= count)
                        return Created[count - 1].ActionId;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("approval was not requested");
        }
    }

    private sealed class FakePush : IPushService
    {
        public bool IsConfigured { get; set; } = true;

        /// <summary>Сколько отправок «принял FCM»; 0 — у пользователя нет зарегистрированных токенов.</summary>
        public int Result { get; set; } = 1;
        public ConcurrentQueue<Dictionary<string, string>> Sent { get; } = new();

        public Task<int> SendAsync(Guid userId, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
        {
            Sent.Enqueue(new Dictionary<string, string>(data));
            return Task.FromResult(Result);
        }

        public async Task<Dictionary<string, string>?> WaitForAsync(string type)
        {
            for (var i = 0; i < 200; i++)
            {
                var hit = Sent.FirstOrDefault(d => d.GetValueOrDefault("type") == type);
                if (hit is not null)
                    return hit;
                await Task.Delay(10);
            }
            return null;
        }
    }

    private static (PhoneApprovalService Service, FakePending Pending, FakePush Push, PhoneToolBridge Bridge) NewApprovals(TimeSpan? timeout = null)
    {
        var pending = new FakePending();
        var push = new FakePush();
        var bridge = NewBridge();
        var service = new PhoneApprovalService(pending, bridge, push, new RecordingHubContext(),
            NullLogger<PhoneApprovalService>.Instance, timeout);
        return (service, pending, push, bridge);
    }

    private const string DeleteArgs = "{\"path\":\"/storage/emulated/0/DCIM/a.jpg\"}";

    private static async Task ApprovalOutcomesAsync()
    {
        var (service, pending, _, _) = NewApprovals();
        var user = Guid.NewGuid();
        var task = Guid.NewGuid();

        var approve = service.RequestApprovalAsync(user, task, task, "phone_clear_cache", "{\"package\":\"com.foo\"}", CancellationToken.None);
        var id1 = await pending.WaitForCreatedAsync(1);
        Assert(await service.DecideAsync(user, id1, true, CancellationToken.None) == PhoneDecisionResult.Applied, "decision applied");
        Assert(await approve == PhoneApprovalOutcome.Approved, "approved");
        Assert(pending.Created[0].Command.Contains("Очистить кэш") && !pending.Created[0].Dangerous, "title, not dangerous for C");

        var deny = service.RequestApprovalAsync(user, task, task, "phone_file_delete", DeleteArgs, CancellationToken.None);
        var id2 = await pending.WaitForCreatedAsync(2);
        Assert(pending.Created[1].Dangerous && pending.Created[1].Command.Contains("/storage/emulated/0/DCIM/a.jpg"), "D is dangerous with a readable title");
        await service.DecideAsync(user, id2, false, CancellationToken.None);
        Assert(await deny == PhoneApprovalOutcome.Denied, "denied");

        var (fast, fastPending, _, _) = NewApprovals(TimeSpan.FromMilliseconds(150));
        var timeout = await fast.RequestApprovalAsync(user, task, task, "phone_uninstall_app", "{\"package\":\"com.foo\"}", CancellationToken.None);
        Assert(timeout == PhoneApprovalOutcome.TimedOut, "no answer in time is a refusal");
        var late = await fast.DecideAsync(user, fastPending.Created[0].ActionId, true, CancellationToken.None);
        Assert(late == PhoneDecisionResult.AlreadyResolved, "a late decision after the timeout has no effect");

        using var cts = new CancellationTokenSource();
        var cancelled = service.RequestApprovalAsync(user, task, task, "phone_file_mkdir", "{\"path\":\"a\"}", cts.Token);
        await pending.WaitForCreatedAsync(3);
        cts.Cancel();
        var threw = false;
        try { await cancelled; } catch (OperationCanceledException) { threw = true; }
        Assert(threw, "stopping the task cancels the wait");
    }

    private static async Task ApprovalIdempotentAsync()
    {
        var (service, pending, _, _) = NewApprovals();
        var user = Guid.NewGuid();
        var task = Guid.NewGuid();
        var run = service.RequestApprovalAsync(user, task, task, "phone_force_stop_app", "{\"package\":\"com.foo\"}", CancellationToken.None);
        var id = await pending.WaitForCreatedAsync();

        Assert(await service.DecideAsync(Guid.NewGuid(), id, true, CancellationToken.None) == PhoneDecisionResult.NotFound,
            "another user cannot decide (and does not learn the action exists)");
        Assert(!run.IsCompleted, "a foreign decision changes nothing");
        Assert(await service.DecideAsync(user, Guid.NewGuid(), true, CancellationToken.None) == PhoneDecisionResult.NotFound, "unknown action");

        Assert(await service.DecideAsync(user, id, false, CancellationToken.None) == PhoneDecisionResult.Applied, "owner decides");
        Assert(await run == PhoneApprovalOutcome.Denied, "denied");
        Assert(await service.DecideAsync(user, id, true, CancellationToken.None) == PhoneDecisionResult.AlreadyResolved, "second decision — conflict");
        Assert(await service.DecideAsync(Guid.NewGuid(), id, true, CancellationToken.None) == PhoneDecisionResult.NotFound,
            "a foreign user still sees nothing for a resolved action");
    }

    private static async Task ApprovalPushAsync()
    {
        var (service, pending, push, bridge) = NewApprovals();
        var user = Guid.NewGuid();
        var task = Guid.NewGuid();
        var chat = Guid.NewGuid();

        // Телефон офлайн — push обязателен.
        var run = service.RequestApprovalAsync(user, task, chat, "phone_uninstall_app", "{\"package\":\"com.foo\"}", CancellationToken.None);
        var id = await pending.WaitForCreatedAsync();
        var data = await push.WaitForAsync("confirm");
        Assert(data is not null, "confirm push when the phone is offline");
        Assert(data!["actionId"] == id.ToString() && data["taskId"] == task.ToString() && data["chatId"] == chat.ToString(), "push ids match");
        Assert(data["dangerous"] == "true" && data["title"].Contains("com.foo"), "dangerous flag and title");
        await service.DecideAsync(user, id, true, CancellationToken.None);
        await run;

        // Приложение на экране — только карточка в приложении.
        bridge.Register(user, "c", Info);
        bridge.SetForeground(user, "c", true);
        var run2 = service.RequestApprovalAsync(user, task, chat, "phone_clear_cache", "{}", CancellationToken.None);
        var id2 = await pending.WaitForCreatedAsync(2);
        await Task.Delay(100);
        Assert(push.Sent.Count == 1, "no second push while the app is on screen");
        await service.DecideAsync(user, id2, true, CancellationToken.None);
        await run2;

        // В фоне — снова push, и он не dangerous для C.
        bridge.SetForeground(user, "c", false);
        var run3 = service.RequestApprovalAsync(user, task, chat, "phone_clear_cache", "{}", CancellationToken.None);
        var id3 = await pending.WaitForCreatedAsync(3);
        for (var i = 0; i < 100 && push.Sent.Count < 2; i++)
            await Task.Delay(10);
        Assert(push.Sent.Count == 2 && push.Sent.Last()["dangerous"] == "false", "background — push again, C is not dangerous");
        await service.DecideAsync(user, id3, false, CancellationToken.None);
        await run3;
    }

    private static async Task DismissAsync()
    {
        var user = Guid.NewGuid();
        var task = Guid.NewGuid();
        var chat = Guid.NewGuid();
        const string args = "{\"package\":\"com.foo\"}";

        // Решение в вебе/приложении (хаб → IPendingActionService) — уведомление нужно убрать, и после confirm.
        var (service, pending, push, _) = NewApprovals();
        var run = service.RequestApprovalAsync(user, task, chat, "phone_clear_cache", args, CancellationToken.None);
        var id = await pending.WaitForCreatedAsync();
        Assert(await push.WaitForAsync("confirm") is not null, "confirm push was sent");
        Assert(await pending.ConfirmActionAsync(id, user, true, false, CancellationToken.None), "hub decision applied");
        Assert(await run == PhoneApprovalOutcome.Approved, "approved via hub");
        var dismiss = await push.WaitForAsync("dismiss");
        Assert(dismiss is not null && dismiss["actionId"] == id.ToString() && dismiss.Count == 2, "dismiss carries only type and actionId");
        Assert(push.Sent.Select(d => d["type"]).SequenceEqual(new[] { "confirm", "dismiss" }), "dismiss goes after confirm");

        // Решение из самого уведомления (REST) — система уже закрыла его, dismiss не нужен.
        (service, pending, push, _) = NewApprovals();
        run = service.RequestApprovalAsync(user, task, chat, "phone_clear_cache", args, CancellationToken.None);
        id = await pending.WaitForCreatedAsync();
        await push.WaitForAsync("confirm");
        Assert(await service.DecideAsync(user, id, false, CancellationToken.None) == PhoneDecisionResult.Applied, "REST decision applied");
        Assert(await run == PhoneApprovalOutcome.Denied, "denied via the notification");
        await Task.Delay(150);
        Assert(push.Sent.All(d => d["type"] != "dismiss"), "no dismiss after a decision made in the notification");

        // Таймаут — уведомление устарело.
        (service, pending, push, _) = NewApprovals(TimeSpan.FromMilliseconds(150));
        Assert(await service.RequestApprovalAsync(user, task, chat, "phone_clear_cache", args, CancellationToken.None) == PhoneApprovalOutcome.TimedOut, "timed out");
        dismiss = await push.WaitForAsync("dismiss");
        Assert(dismiss is not null && dismiss["actionId"] == pending.Created[0].ActionId.ToString(), "dismiss after the timeout");

        // Приложение на экране — confirm-pushа не было, значит и dismiss не нужен.
        (service, pending, push, var bridge) = NewApprovals();
        bridge.Register(user, "c", Info);
        bridge.SetForeground(user, "c", true);
        run = service.RequestApprovalAsync(user, task, chat, "phone_clear_cache", args, CancellationToken.None);
        id = await pending.WaitForCreatedAsync();
        await pending.ConfirmActionAsync(id, user, true, false, CancellationToken.None);
        await run;
        await Task.Delay(150);
        Assert(push.Sent.IsEmpty, "foreground app: neither confirm nor dismiss");

        // Нет зарегистрированных токенов (FCM ничего не принял) — убирать нечего.
        (service, pending, push, _) = NewApprovals();
        push.Result = 0;
        run = service.RequestApprovalAsync(user, task, chat, "phone_clear_cache", args, CancellationToken.None);
        id = await pending.WaitForCreatedAsync();
        await push.WaitForAsync("confirm");
        await pending.ConfirmActionAsync(id, user, true, false, CancellationToken.None);
        await run;
        await Task.Delay(150);
        Assert(push.Sent.Select(d => d["type"]).SequenceEqual(new[] { "confirm" }), "no tokens: no dismiss");
    }

    // ---- catalog ----

    private static Task CatalogAsync()
    {
        var expected = new[]
        {
            "phone_device_info", "phone_permissions", "phone_request_permission", "phone_apps_list", "phone_battery_report",
            "phone_storage_report", "phone_open_settings", "phone_launch_app", "phone_open_url", "phone_uninstall_app",
            "phone_clear_cache", "phone_force_stop_app", "phone_screen_read", "phone_screenshot", "phone_tap", "phone_swipe",
            "phone_type_text", "phone_global_action", "phone_gallery_list", "phone_gallery_pick", "phone_image_view",
            "phone_widget_photo", "phone_set_wallpaper", "phone_file_list", "phone_file_read", "phone_file_search",
            "phone_file_write", "phone_file_mkdir", "phone_file_copy", "phone_file_move", "phone_file_delete",
            "phone_share", "phone_clipboard_set",
        };
        var names = PhoneToolCatalog.All.Select(t => t.Name).ToList();
        Assert(names.OrderBy(n => n).SequenceEqual(expected.OrderBy(n => n)), $"the catalog matches the contract table, got {names.Count}");
        Assert(names.Distinct().Count() == names.Count, "no duplicates");

        foreach (var schema in PhoneToolCatalog.Schemas())
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(schema));
            var fn = doc.RootElement.GetProperty("function");
            var name = fn.GetProperty("name").GetString()!;
            Assert(doc.RootElement.GetProperty("type").GetString() == "function", $"{name}: function schema");
            Assert(fn.GetProperty("description").GetString()!.Length > 20, $"{name}: description");
            var p = fn.GetProperty("parameters");
            Assert(p.GetProperty("type").GetString() == "object" && p.TryGetProperty("properties", out _), $"{name}: object parameters");
        }

        var confirm = new[] { "phone_clear_cache", "phone_force_stop_app", "phone_set_wallpaper", "phone_file_write", "phone_file_mkdir", "phone_file_copy", "phone_file_move" };
        var dangerous = new[] { "phone_uninstall_app", "phone_file_delete" };
        foreach (var name in expected)
        {
            var want = dangerous.Contains(name) ? PhoneToolClass.Dangerous : confirm.Contains(name) ? PhoneToolClass.Confirm : PhoneToolClass.Read;
            Assert(PhoneToolCatalog.Classify(name) == want, $"{name}: class {want}");
            Assert(PhoneToolCatalog.Label(name).EndsWith("…"), $"{name}: status label");
        }
        Assert(PhoneToolCatalog.Classify("phone_unknown") == PhoneToolClass.Dangerous, "unknown tools are never treated as read-only");
        Assert(!PhoneToolCatalog.IsPhoneTool("bash") && !PhoneToolCatalog.IsPhoneTool("phone_unknown") && PhoneToolCatalog.IsPhoneTool("phone_tap"), "IsPhoneTool");

        var swipe = PhoneToolCatalog.Get("phone_apps_list")!;
        Assert(swipe.Params.Single(x => x.Name == "sort").Enum!.SequenceEqual(new[] { "last_used", "size", "name", "foreground_time" }), "enum from the contract");
        Assert(PhoneToolCatalog.Permissions.Length == 15 && PhoneToolCatalog.SettingsScreens.Length == 19, "permission and screen lists");
        return Task.CompletedTask;
    }

    private static Task ArgumentsAsync()
    {
        bool Ok(string tool, string json, out string normalized, out string? error) =>
            PhoneToolCatalog.TryNormalizeArguments(tool, json, out normalized, out error);

        Assert(Ok("phone_permissions", "", out var n0, out _) && n0 == "{}", "empty arguments are an empty object");
        Assert(!Ok("phone_launch_app", "{broken", out _, out var e1) && e1!.Contains("JSON"), "invalid JSON");
        Assert(!Ok("phone_launch_app", "[1]", out _, out _), "array is not an object");
        Assert(!Ok("phone_launch_app", "{\"package\":\"  \"}", out _, out _), "blank required string");
        Assert(!Ok("phone_launch_app", "{\"package\":5}", out _, out _), "wrong type");
        Assert(!Ok("phone_request_permission", "{\"permission\":\"root\"}", out _, out var e2) && e2!.Contains("camera"), "enum is enforced");
        Assert(Ok("phone_request_permission", "{\"permission\":\"camera\"}", out var n1, out _) && n1 == "{\"permission\":\"camera\"}", "valid enum");
        Assert(Ok("phone_tap", "{\"x\":10.4,\"y\":20,\"long_press\":true,\"extra\":\"x\"}", out var n2, out _)
               && n2.Contains("\"x\":10") && !n2.Contains("extra"), "numbers normalized, unknown dropped");
        Assert(!Ok("phone_tap", "{\"long_press\":\"yes\"}", out _, out _), "boolean type");
        Assert(Ok("phone_file_write", "{\"path\":\"a.txt\",\"content\":\"\"}", out _, out _), "empty content is allowed");
        Assert(!Ok("phone_file_write", "{\"path\":\"a.txt\",\"content\":\"" + new string('x', 200_001) + "\"}", out _, out _), "content cap");
        Assert(Ok("phone_apps_list", "{\"limit\":null}", out var n3, out _) && n3 == "{}", "null optional is dropped");

        var (title, _) = PhoneToolCatalog.Describe("phone_uninstall_app", "{\"package\":\"com.foo\"}");
        Assert(title == "Удалить приложение com.foo", title);
        Assert(PhoneToolCatalog.Describe("phone_clear_cache", "{}").Title == "Очистить кэш", "clear cache title");
        Assert(PhoneToolCatalog.Describe("phone_file_delete", "{\"path\":\"/storage/x\",\"recursive\":true}").Details.Contains("содержимым"), "recursive delete warns");
        return Task.CompletedTask;
    }

    private static Task ImageOutputAsync()
    {
        var png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 });
        Assert(ToolImageOutput.TryParse($"IMAGE_BASE64:image/png:{png}\n1080x2400", out var mime, out var b64, out var note, out _)
               && mime == "image/png" && b64 == png && note == "1080x2400", "image and note parsed");
        Assert(!ToolImageOutput.TryParse("IMAGE_BASE64:text/html:" + png, out _, out _, out _, out var e1) && e1 is not null, "non-image mime refused");
        Assert(!ToolImageOutput.TryParse("IMAGE_BASE64:image/png:%%%", out _, out _, out _, out _), "broken base64 refused");
        var huge = new string('A', ((int)(ImageContent.MaxBytes * 4 / 3) / 4 + 100) * 4);
        Assert(!ToolImageOutput.TryParse("IMAGE_BASE64:image/jpeg:" + huge, out _, out _, out _, out var e2) && e2!.Contains("большое"), "size limit");
        var (m2, _, _) = ToolImageOutput.Split("IMAGE_BASE64:" + png);
        Assert(m2 == "image/png", "default mime when the payload has none");

        var bridge = NewBridge();
        Assert(PhoneNotifier.Preview("# Заголовок\n\n**Готово**, ```code``` фото `a`") == "Заголовок Готово, фото a", "preview strips markup");
        Assert(PhoneNotifier.Preview(new string('я', 500)).Length <= 141, "preview is short");
        _ = bridge;
        return Task.CompletedTask;
    }

    // ---- notifier ----

    private static async Task NotifierAsync()
    {
        var bridge = NewBridge();
        var push = new FakePush();
        var services = new ServiceCollection().BuildServiceProvider();
        var notifier = new PhoneNotifier(bridge, push, services.GetRequiredService<IServiceScopeFactory>(), new RecordingHubContext(),
            NullLogger<PhoneNotifier>.Instance, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(120));
        var user = Guid.NewGuid();
        var task = Guid.NewGuid();

        var lost = await notifier.EnsurePhoneOnlineAsync(user, task, task, CancellationToken.None);
        Assert(!lost, "no phone after both waits");
        var needed = push.Sent.FirstOrDefault(d => d["type"] == "phone_needed");
        Assert(needed is not null && needed["taskId"] == task.ToString(), "phone_needed push after the first wait");

        var pushWait = notifier.EnsurePhoneOnlineAsync(user, task, task, CancellationToken.None);
        await Task.Delay(120);
        bridge.Register(user, "c", Info);
        Assert(await pushWait, "the phone came back while we were waiting");
        Assert(await notifier.EnsurePhoneOnlineAsync(user, task, task, CancellationToken.None), "online — no waiting");

        var before = push.Sent.Count;
        bridge.SetForeground(user, "c", true);
        await notifier.NotifyCompletedAsync(user, task, task, "ответ", false, CancellationToken.None);
        Assert(push.Sent.Count == before, "no completed push while the app is on screen");

        bridge.SetForeground(user, "c", false);
        await notifier.NotifyCompletedAsync(user, task, task, "Всё **готово**", false, CancellationToken.None);
        var done = push.Sent.Last();
        Assert(done["type"] == "completed" && done["body"] == "Всё готово" && done["chatId"] == task.ToString(), "completed push with the start of the answer");

        await notifier.NotifyCompletedAsync(user, task, task, "секретный ответ", true, CancellationToken.None);
        var neutral = push.Sent.Last();
        Assert(!neutral["body"].Contains("секретный") && neutral["chatId"].Length == 0, "incognito: neutral body, no chat id");

        push.IsConfigured = false;
        var count = push.Sent.Count;
        await notifier.NotifyCompletedAsync(user, task, task, "x", false, CancellationToken.None);
        Assert(push.Sent.Count == count, "nothing is sent when push is not configured");
    }

    // ---- mobile controller ----

    private static DbConexy NewDb() =>
        new(new DbContextOptionsBuilder<DbConexy>().UseTestDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeApprovals : IPhoneApprovalService
    {
        public PhoneDecisionResult Result { get; set; }
        public (Guid User, Guid Action, bool Approved)? Last { get; private set; }

        public Task<PhoneApprovalOutcome> RequestApprovalAsync(Guid userId, Guid taskId, Guid chatId, string tool, string normalizedArguments, CancellationToken ct) =>
            Task.FromResult(PhoneApprovalOutcome.Approved);

        public Task<PhoneDecisionResult> DecideAsync(Guid userId, Guid actionId, bool approved, CancellationToken ct)
        {
            Last = (userId, actionId, approved);
            return Task.FromResult(Result);
        }

        public Task<string?> AskQuestionAsync(Guid userId, Guid taskId, string? question, string[]? options, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public bool AnswerQuestion(Guid userId, Guid requestId, string? answer) => false;
    }

    private static MobileController NewController(DbConexy db, Guid user, FakeApprovals? approvals = null) =>
        new(new MobilePushTokenRepository(db), approvals ?? new FakeApprovals(), NullLogger<MobileController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.ToString()) }, "test")),
                },
            },
        };

    private static string Token(int i) => $"fcm-token-{i:D4}-" + new string('x', 40);

    private static async Task TokensAsync()
    {
        await using var db = NewDb();
        var me = Guid.NewGuid();
        var controller = NewController(db, me);

        for (var i = 0; i < 12; i++)
        {
            var result = await controller.RegisterToken(new PushTokenRequest(Token(i), "android"), CancellationToken.None);
            Assert(result is OkObjectResult, $"token {i} accepted");
            await Task.Delay(5);
        }
        var tokens = await new MobilePushTokenRepository(db).ListTokensAsync(me);
        Assert(tokens.Count == IMobilePushTokenRepository.MaxTokensPerUser, $"capped at 10, got {tokens.Count}");
        Assert(tokens.Contains(Token(11)) && tokens.Contains(Token(2)), "the newest tokens stay");
        Assert(!tokens.Contains(Token(0)) && !tokens.Contains(Token(1)), "the oldest tokens are evicted");

        await controller.RegisterToken(new PushTokenRequest(Token(5), "android"), CancellationToken.None);
        Assert((await db.MobilePushTokens.CountAsync(t => t.UserId == me)) == 10, "a repeated token is not duplicated");

        var bad1 = await controller.RegisterToken(new PushTokenRequest("short", "android"), CancellationToken.None);
        var bad2 = await controller.RegisterToken(new PushTokenRequest("has space " + new string('x', 30), "android"), CancellationToken.None);
        var bad3 = await controller.RegisterToken(null, CancellationToken.None);
        Assert(bad1 is BadRequestObjectResult && bad2 is BadRequestObjectResult && bad3 is BadRequestObjectResult, "invalid tokens are rejected");

        // Токен, который переехал на другой аккаунт на том же телефоне, принадлежит новому владельцу.
        var other = Guid.NewGuid();
        await NewController(db, other).RegisterToken(new PushTokenRequest(Token(11), "android"), CancellationToken.None);
        Assert(!(await new MobilePushTokenRepository(db).ListTokensAsync(me)).Contains(Token(11)), "the token moved to the new account");
        Assert((await new MobilePushTokenRepository(db).ListTokensAsync(other)).Single() == Token(11), "and belongs to it");

        var foreignDelete = await controller.RemoveToken(new PushTokenRequest(Token(11), null), CancellationToken.None);
        Assert(foreignDelete is NoContentResult && (await db.MobilePushTokens.CountAsync(t => t.UserId == other)) == 1,
            "a user cannot delete somebody else's token");
        await controller.RemoveToken(new PushTokenRequest(Token(5), null), CancellationToken.None);
        Assert(!(await new MobilePushTokenRepository(db).ListTokensAsync(me)).Contains(Token(5)), "own token deleted on logout");

        await new MobilePushTokenRepository(db).RemoveTokensAsync(new[] { Token(11) });
        Assert((await db.MobilePushTokens.CountAsync(t => t.UserId == other)) == 0, "dead tokens are removed in bulk");
    }

    private static async Task ActionEndpointAsync()
    {
        await using var db = NewDb();
        var me = Guid.NewGuid();
        var fake = new FakeApprovals();
        var controller = NewController(db, me, fake);
        var id = Guid.NewGuid();

        fake.Result = PhoneDecisionResult.Applied;
        Assert(await controller.Decide(id.ToString(), new ActionDecisionRequest(true), CancellationToken.None) is OkObjectResult, "200 when applied");
        Assert(fake.Last is { Approved: true } last && last.User == me && last.Action == id, "the decision carries the caller's id from the token");

        fake.Result = PhoneDecisionResult.AlreadyResolved;
        Assert(await controller.Decide(id.ToString(), new ActionDecisionRequest(false), CancellationToken.None) is ConflictObjectResult, "409 when already resolved");
        fake.Result = PhoneDecisionResult.NotFound;
        Assert(await controller.Decide(id.ToString(), new ActionDecisionRequest(true), CancellationToken.None) is NotFoundObjectResult, "404 for unknown or foreign");
        Assert(await controller.Decide("not-a-guid", new ActionDecisionRequest(true), CancellationToken.None) is BadRequestObjectResult, "400 for a non-guid id");
        Assert(await controller.Decide(id.ToString(), null, CancellationToken.None) is BadRequestObjectResult, "400 without a body");

        var anonymous = new MobileController(new MobilePushTokenRepository(db), fake, NullLogger<MobileController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        Assert(await anonymous.Decide(id.ToString(), new ActionDecisionRequest(true), CancellationToken.None) is UnauthorizedResult, "401 without a user");
        Assert(typeof(MobileController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Length == 1, "controller is [Authorize]");
    }

    // ---- push ----

    private static Task FcmHelpersAsync()
    {
        using var rsa = RSA.Create(2048);
        var jwt = FcmPushService.BuildAssertion("svc@proj.iam.gserviceaccount.com", rsa, "https://oauth2.googleapis.com/token",
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var parts = jwt.Split('.');
        Assert(parts.Length == 3 && !jwt.Contains('=') && !jwt.Contains('+') && !jwt.Contains('/'), "three base64url parts");

        static byte[] Decode(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        var c = claims.RootElement;
        Assert(c.GetProperty("iss").GetString() == "svc@proj.iam.gserviceaccount.com"
               && c.GetProperty("scope").GetString() == "https://www.googleapis.com/auth/firebase.messaging"
               && c.GetProperty("aud").GetString() == "https://oauth2.googleapis.com/token"
               && c.GetProperty("exp").GetInt64() > c.GetProperty("iat").GetInt64(), "claims");
        Assert(rsa.VerifyData(System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "the RS256 signature verifies with the public key");

        Assert(FcmPushService.IsUnregistered(System.Net.HttpStatusCode.NotFound, ""), "404 is a dead token");
        Assert(FcmPushService.IsUnregistered(System.Net.HttpStatusCode.BadRequest,
            """{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}"""), "UNREGISTERED");
        Assert(!FcmPushService.IsUnregistered(System.Net.HttpStatusCode.InternalServerError, """{"error":{"status":"INTERNAL"}}"""), "other errors keep the token");
        Assert(!FcmPushService.IsUnregistered(System.Net.HttpStatusCode.BadRequest, "garbage"), "garbage keeps the token");

        var json = "{\"client_email\":\"a@b\",\"private_key\":\"KEY\"}";
        Assert(FcmPushService.ParseAccount(json) is { ClientEmail: "a@b", TokenUri: "https://oauth2.googleapis.com/token" }, "service account JSON parsed");
        var file = Path.Combine(Path.GetTempPath(), "conexy-sa-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, json);
        try { Assert(FcmPushService.ParseAccount(file)?.ClientEmail == "a@b", "a path to the file works too"); }
        finally { File.Delete(file); }
        Assert(FcmPushService.ParseAccount("") is null, "empty config — not configured");
        return Task.CompletedTask;
    }
}
