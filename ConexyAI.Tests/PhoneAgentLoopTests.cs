using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Model;
using ConexyAI.Service;
using ConexyAI.Service.Device;
using ConexyAI.Service.Phone;

// PHONE_AGENT: агентский цикл с телефоном — выдача инструментов, подтверждения C/D, обрыв связи.
internal static class PhoneAgentLoopTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("phone loop: without UsePhone no phone_* tool is advertised or allowed", NoUsePhoneAsync);
        TestRegistry.Add("phone loop: UsePhone without a connected phone advertises no phone_* schemas", OfflinePhoneAsync);
        TestRegistry.Add("phone loop: UsePhone with an online phone advertises phone_*, chat gets the phone profile", OnlinePhoneAsync);
        TestRegistry.Add("phone loop: a refused C/D action is USER_REJECTED and never reaches the phone", RejectedAsync);
        TestRegistry.Add("phone loop: an approved C/D action runs on the phone", ApprovedAsync);
        TestRegistry.Add("phone loop: after a connection drop later phone calls fail without retries", ConnectionDropAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private sealed class FakeBridge : IPhoneToolBridge
    {
        public bool Online { get; set; } = true;
        public List<(string Tool, string Arguments)> Executed { get; } = new();
        public Func<string, DeviceToolOutcome> Respond { get; set; } = _ => new DeviceToolOutcome("ok", false);

        public bool Register(Guid userId, string connectionId, string? infoJson) => true;
        public bool SetForeground(Guid userId, string connectionId, bool foreground) => true;
        public bool IsOnline(Guid userId) => Online;
        public bool IsForeground(Guid userId) => false;
        public bool IsPhoneConnection(Guid userId, string connectionId) => Online;
        public PhoneInfo? GetInfo(Guid userId) => null;
        public Task<bool> WaitForOnlineAsync(Guid userId, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Online);

        public Task<DeviceToolOutcome> ExecuteAsync(Guid userId, Guid taskId, string tool, string argumentsJson, CancellationToken ct)
        {
            lock (Executed)
                Executed.Add((tool, argumentsJson));
            return Task.FromResult(Respond(tool));
        }

        public bool Complete(string connectionId, Guid callId, string output, bool isError) => false;
        public void Unregister(string connectionId)
        {
        }
    }

    private sealed class FakeApprovals : IPhoneApprovalService
    {
        public PhoneApprovalOutcome Decision { get; set; } = PhoneApprovalOutcome.Approved;
        public List<string> Requests { get; } = new();

        public Task<PhoneApprovalOutcome> RequestApprovalAsync(
            Guid userId, Guid taskId, Guid chatId, string tool, string normalizedArguments, CancellationToken ct)
        {
            Requests.Add(tool);
            return Task.FromResult(Decision);
        }

        public Task<PhoneDecisionResult> DecideAsync(Guid userId, Guid actionId, bool approved, CancellationToken ct) =>
            Task.FromResult(PhoneDecisionResult.NotFound);

        public Task<string?> AskQuestionAsync(Guid userId, Guid taskId, string? question, string[]? options, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public bool AnswerQuestion(Guid userId, Guid requestId, string? answer) => false;
    }

    private static string ToolMessages(AgentCapabilityTests.ScriptLlm llm) =>
        string.Join("\n", llm.Requests.Last().Where(m => m.Role == "tool").Select(m => m.Content?.ToString()));

    private static string[] PhoneToolsIn(AgentCapabilityTests.ScriptLlm llm) =>
        llm.AdvertisedTools.Where(PhoneToolCatalog.IsPhoneTool).ToArray();

    private static async Task NoUsePhoneAsync()
    {
        var root = AgentCapabilityTests.CreateTempRoot();
        try
        {
            var bridge = new FakeBridge();
            var approvals = new FakeApprovals();
            var llm = new AgentCapabilityTests.ScriptLlm(
                AgentCapabilityTests.Turn(AgentCapabilityTests.Call("c1", "phone_clear_cache", "{\"package\":\"com.foo\"}")),
                AgentCapabilityTests.Text("готово"));
            var (runner, job, context) = AgentCapabilityTests.CreateRunner(root, llm, new RecordingHubContext(),
                phoneBridge: bridge, phoneApprovals: approvals);

            await runner.RunLoopAsync(job, context, AgentCapabilityTests.Guard());

            Assert(PhoneToolsIn(llm).Length == 0, "no phone_* schema without UsePhone");
            Assert(!llm.AdvertisedTools.Contains("ask_user"), "no phone ask_user without UsePhone");
            foreach (var tool in PhoneToolCatalog.All.Select(t => t.Name))
                Assert(!runner.IsToolAllowed(tool), $"{tool} is refused without UsePhone");
            Assert(bridge.Executed.Count == 0 && approvals.Requests.Count == 0, "a hallucinated phone call reaches neither the phone nor the approvals");
            Assert(ToolMessages(llm).Contains("not available"), "the model is told the tool is unavailable");
        }
        finally
        {
            AgentCapabilityTests.DeleteDir(root);
        }
    }

    private static async Task OfflinePhoneAsync()
    {
        var root = AgentCapabilityTests.CreateTempRoot();
        try
        {
            var bridge = new FakeBridge { Online = false };
            var llm = new AgentCapabilityTests.ScriptLlm(AgentCapabilityTests.Text("ok"));
            var (runner, job, context) = AgentCapabilityTests.CreateRunner(root, llm, new RecordingHubContext(),
                usePhone: true, phoneBridge: bridge, phoneApprovals: new FakeApprovals());

            await runner.RunLoopAsync(job, context, AgentCapabilityTests.Guard());

            Assert(PhoneToolsIn(llm).Length == 0, "no phone_* schemas while the phone is not connected");
            Assert(!llm.AdvertisedTools.Contains("ask_user"), "no ask_user while the phone is not connected");
        }
        finally
        {
            AgentCapabilityTests.DeleteDir(root);
        }
    }

    private static async Task OnlinePhoneAsync()
    {
        var root = AgentCapabilityTests.CreateTempRoot();
        try
        {
            var bridge = new FakeBridge();
            var llm = new AgentCapabilityTests.ScriptLlm(AgentCapabilityTests.Text("ok"));
            var (runner, job, context) = AgentCapabilityTests.CreateRunner(root, llm, new RecordingHubContext(),
                ConexyModelType.ConexyV1Flash, usePhone: true, phoneBridge: bridge, phoneApprovals: new FakeApprovals());

            await runner.RunLoopAsync(job, context, AgentCapabilityTests.Guard());

            var expected = PhoneToolCatalog.All.Select(t => t.Name).OrderBy(n => n);
            Assert(PhoneToolsIn(llm).OrderBy(n => n).SequenceEqual(expected), "every phone_* schema is advertised");
            Assert(llm.AdvertisedTools.Contains("ask_user"), "ask_user is advertised with the phone");
            Assert(runner.ProfileMode == "phone", $"a phone chat uses the phone profile, was '{runner.ProfileMode}'");
            Assert(runner.IsToolAllowed("phone_screenshot"), "phone_screenshot is allowed");

            // Агентский режим сохраняет свой профиль, но phone_* тоже получает.
            var coderLlm = new AgentCapabilityTests.ScriptLlm(AgentCapabilityTests.Text("ok"));
            var (coder, coderJob, coderContext) = AgentCapabilityTests.CreateRunner(root, coderLlm, new RecordingHubContext(),
                usePhone: true, phoneBridge: bridge, phoneApprovals: new FakeApprovals());
            await coder.RunLoopAsync(coderJob, coderContext, AgentCapabilityTests.Guard());
            Assert(coder.ProfileMode == "coder" && PhoneToolsIn(coderLlm).Length == expected.Count(), "Coder keeps its profile and gets phone_*");
        }
        finally
        {
            AgentCapabilityTests.DeleteDir(root);
        }
    }

    private static async Task RejectedAsync()
    {
        var root = AgentCapabilityTests.CreateTempRoot();
        try
        {
            var bridge = new FakeBridge();
            var approvals = new FakeApprovals { Decision = PhoneApprovalOutcome.Denied };
            var llm = new AgentCapabilityTests.ScriptLlm(
                AgentCapabilityTests.Turn(
                    AgentCapabilityTests.Call("c1", "phone_file_delete", "{\"path\":\"/storage/emulated/0/DCIM/a.jpg\"}"),
                    AgentCapabilityTests.Call("c2", "phone_clear_cache", "{\"package\":\"com.foo\"}")),
                AgentCapabilityTests.Text("понял"));
            var (runner, job, context) = AgentCapabilityTests.CreateRunner(root, llm, new RecordingHubContext(),
                usePhone: true, phoneBridge: bridge, phoneApprovals: approvals);

            await runner.RunLoopAsync(job, context, AgentCapabilityTests.Guard());

            Assert(approvals.Requests.SequenceEqual(new[] { "phone_file_delete", "phone_clear_cache" }), "both D and C actions asked for approval");
            Assert(bridge.Executed.Count == 0, "a rejected action is never executed on the phone");
            Assert(CountOf(ToolMessages(llm), "USER_REJECTED") == 2, "the model gets USER_REJECTED for both calls");
        }
        finally
        {
            AgentCapabilityTests.DeleteDir(root);
        }
    }

    private static async Task ApprovedAsync()
    {
        var root = AgentCapabilityTests.CreateTempRoot();
        try
        {
            var bridge = new FakeBridge();
            var approvals = new FakeApprovals { Decision = PhoneApprovalOutcome.Approved };
            var llm = new AgentCapabilityTests.ScriptLlm(
                AgentCapabilityTests.Turn(
                    AgentCapabilityTests.Call("c1", "phone_file_delete", "{\"path\":\"/storage/emulated/0/DCIM/a.jpg\"}"),
                    AgentCapabilityTests.Call("c2", "phone_battery_report", "{}")),
                AgentCapabilityTests.Text("готово"));
            var (runner, job, context) = AgentCapabilityTests.CreateRunner(root, llm, new RecordingHubContext(),
                usePhone: true, phoneBridge: bridge, phoneApprovals: approvals);

            await runner.RunLoopAsync(job, context, AgentCapabilityTests.Guard());

            Assert(approvals.Requests.SequenceEqual(new[] { "phone_file_delete" }), "only the D action asks; a read tool does not");
            Assert(bridge.Executed.Select(e => e.Tool).OrderBy(n => n).SequenceEqual(new[] { "phone_battery_report", "phone_file_delete" }),
                "approved and read actions run on the phone");
            Assert(!ToolMessages(llm).Contains("USER_REJECTED"), "no rejection reported");
        }
        finally
        {
            AgentCapabilityTests.DeleteDir(root);
        }
    }

    private static async Task ConnectionDropAsync()
    {
        var root = AgentCapabilityTests.CreateTempRoot();
        try
        {
            var bridge = new FakeBridge
            {
                Respond = _ => new DeviceToolOutcome(PhoneToolBridge.OfflineMessage, true),
            };
            var approvals = new FakeApprovals();
            var llm = new AgentCapabilityTests.ScriptLlm(
                AgentCapabilityTests.Turn(AgentCapabilityTests.Call("c1", "phone_device_info", "{}")),
                AgentCapabilityTests.Turn(
                    AgentCapabilityTests.Call("c2", "phone_battery_report", "{}"),
                    AgentCapabilityTests.Call("c3", "phone_clear_cache", "{\"package\":\"com.foo\"}")),
                AgentCapabilityTests.Text("телефон недоступен"));
            var (runner, job, context) = AgentCapabilityTests.CreateRunner(root, llm, new RecordingHubContext(),
                usePhone: true, phoneBridge: bridge, phoneApprovals: approvals);

            await runner.RunLoopAsync(job, context, AgentCapabilityTests.Guard());

            Assert(bridge.Executed.Count == 1 && bridge.Executed[0].Tool == "phone_device_info",
                $"only the first call reached the phone, got {bridge.Executed.Count}");
            Assert(approvals.Requests.Count == 0, "no approval is requested for a call that cannot run");
            var tools = ToolMessages(llm);
            Assert(CountOf(tools, "не повторяй") >= 2 || CountOf(tools, "НЕ повторяй") >= 2, "later calls are refused with a do-not-retry message");
        }
        finally
        {
            AgentCapabilityTests.DeleteDir(root);
        }
    }

    private static int CountOf(string text, string fragment)
    {
        var count = 0;
        for (var i = text.IndexOf(fragment, StringComparison.Ordinal); i >= 0; i = text.IndexOf(fragment, i + fragment.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
