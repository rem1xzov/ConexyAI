using System.Runtime.CompilerServices;
using ConexyAI.Configuration;
using ConexyAI.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// BACKGROUND_JOB: добавлено 2026-11-20
/// <summary>
/// Фоновые задачи песочницы: запуск долгой команды без ожидания, чтение вывода, остановка и изоляция
/// по чату. Docker не нужен — проверяется через фейковые runner'ы песочницы.
/// </summary>
internal static class BackgroundJobTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("background jobs: start returns a job id and instructions", StartAsync);
        TestRegistry.Add("background jobs: output follows a running job and caches its final output", OutputAsync);
        TestRegistry.Add("background jobs: kill stops the container once", KillAsync);
        TestRegistry.Add("background jobs: jobs are isolated by chat", OwnershipAsync);
        TestRegistry.Add("background jobs: the per-chat limit is enforced", LimitAsync);
        TestRegistry.Add("background jobs: an empty command is refused", EmptyAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task<(BackgroundJobService Service, Guid ChatId, FakeSandbox Sandbox, FakeServices Services, string Root)> SetupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bgjobs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new ConexyWorkspaceService(
            Options.Create(new WorkspaceOptions { RootPath = root }),
            NullLogger<ConexyWorkspaceService>.Instance);
        var chatId = Guid.NewGuid();
        Directory.CreateDirectory(workspace.GetTaskWorkspacePath(chatId));

        var sandbox = new FakeSandbox();
        var services = new FakeServices();
        var jobService = new BackgroundJobService(
            sandbox, services, workspace, new FakeSessions(), NullLogger<BackgroundJobService>.Instance);
        await Task.CompletedTask;
        return (jobService, chatId, sandbox, services, root);
    }

    private static async Task StartAsync()
    {
        var (service, chatId, sandbox, _, root) = await SetupAsync();
        try
        {
            var (started, jobId, message) = await service.StartAsync(chatId, "dotnet build");

            Assert(started, "the job starts: " + message);
            Assert(jobId != Guid.Empty, "a job id is returned");
            Assert(message.Contains("job_output", StringComparison.Ordinal) && message.Contains(jobId.ToString(), StringComparison.Ordinal),
                "the answer tells how to read the job: " + message);
            Assert(sandbox.Started.Count == 1, "one detached container is created");
            Assert(sandbox.Started[0].Contains(chatId.ToString("N"), StringComparison.Ordinal), "the container name is bound to the chat");

            var listed = service.List(chatId).Single();
            Assert(listed.Status == "running" && listed.Command == "dotnet build", "the registry remembers the command");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task OutputAsync()
    {
        var (service, chatId, _, services, root) = await SetupAsync();
        try
        {
            var (_, jobId, _) = await service.StartAsync(chatId, "npm install");
            services.Logs = "installing...\ndone";

            var running = await service.OutputAsync(chatId, jobId);
            Assert(running.Contains("выполняется", StringComparison.Ordinal) && running.Contains("installing", StringComparison.Ordinal),
                "a running job shows its status and logs: " + running);

            services.Running = false;
            var finished = await service.OutputAsync(chatId, jobId);
            Assert(finished.Contains("завершена", StringComparison.Ordinal) && finished.Contains("done", StringComparison.Ordinal),
                "a finished job reports completion and the final output: " + finished);
            Assert(services.Stopped.Count == 1, "the finished container is cleaned up once");

            services.Logs = "should not be shown again";
            var cached = await service.OutputAsync(chatId, jobId);
            Assert(cached.Contains("done", StringComparison.Ordinal) && !cached.Contains("should not be shown again", StringComparison.Ordinal),
                "the final output is cached after the container is removed: " + cached);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task KillAsync()
    {
        var (service, chatId, sandbox, services, root) = await SetupAsync();
        try
        {
            var (_, jobId, _) = await service.StartAsync(chatId, "sleep 600");
            var killed = await service.KillAsync(chatId, jobId);
            Assert(killed.Contains("остановлена", StringComparison.Ordinal), "kill reports the stop: " + killed);
            Assert(services.Stopped.Count == 1 && services.Stopped[0] == sandbox.Started[0], "kill stops exactly this container");

            var again = await service.KillAsync(chatId, jobId);
            Assert(again.Contains("уже завершена", StringComparison.Ordinal), "a second kill does not repeat the stop: " + again);
            Assert(services.Stopped.Count == 1, "the container is stopped once");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task OwnershipAsync()
    {
        var (service, chatId, _, services, root) = await SetupAsync();
        try
        {
            var (_, jobId, _) = await service.StartAsync(chatId, "make all");
            var stranger = Guid.NewGuid();

            Assert((await service.OutputAsync(stranger, jobId)).Contains("не найдена", StringComparison.Ordinal),
                "another chat cannot read the job");
            Assert((await service.KillAsync(stranger, jobId)).Contains("не найдена", StringComparison.Ordinal),
                "another chat cannot kill the job");
            Assert(services.Stopped.Count == 0 && service.List(stranger).Count == 0, "the foreign chat sees nothing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task LimitAsync()
    {
        var (service, chatId, _, _, root) = await SetupAsync();
        try
        {
            for (var index = 0; index < BackgroundJobService.MaxJobsPerChat; index++)
            {
                var (started, _, message) = await service.StartAsync(chatId, $"build {index}");
                Assert(started, $"job {index} starts: {message}");
            }

            var (limited, _, limitMessage) = await service.StartAsync(chatId, "one more");
            Assert(!limited && limitMessage.Contains("лимит", StringComparison.Ordinal),
                "a fourth concurrent job is refused with the limit: " + limitMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task EmptyAsync()
    {
        var (service, chatId, sandbox, _, root) = await SetupAsync();
        try
        {
            var (started, jobId, message) = await service.StartAsync(chatId, "   ");
            Assert(!started && jobId == Guid.Empty && message.Contains("пустая", StringComparison.Ordinal),
                "an empty command is refused: " + message);
            Assert(sandbox.Started.Count == 0, "nothing is started for an empty command");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeSandbox : IDockerSandboxRunner
    {
        public List<string> Started { get; } = new();

        public Task<bool> IsDockerAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<SandboxExecutionResult> RunAsync(
            string command, string workspaceHostPath, TimeSpan timeout, CancellationToken ct = default,
            string? sessionStateHostPath = null) => throw new NotSupportedException("background jobs only");

        public Task<(bool Ok, string Output)> StartBackgroundAsync(
            string command, string workspaceHostPath, string? sessionStateHostPath, string containerName,
            CancellationToken ct = default)
        {
            Started.Add(containerName);
            return Task.FromResult((true, string.Empty));
        }
    }

    private sealed class FakeServices : IDockerServiceRunner
    {
        public bool Running { get; set; } = true;
        public string Logs { get; set; } = "started";
        public List<string> Stopped { get; } = new();

        public Task<(bool Ok, string Output)> StartServiceAsync(
            string command, string workspaceHostPath, string? stateHostPath, string containerName,
            string network, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> TailLogsAsync(string containerName, int lines, CancellationToken ct) =>
            Task.FromResult(Logs);

        public Task<bool> IsRunningAsync(string containerName, CancellationToken ct) => Task.FromResult(Running);

        public Task StopServiceAsync(string containerName)
        {
            Stopped.Add(containerName);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessions : ISandboxSessionStore
    {
        public string GetOrCreateStatePath(Guid sessionId) =>
            Path.Combine(Path.GetTempPath(), "bgjob_state_" + sessionId.ToString("N"));

        public void Touch(Guid sessionId) { }

        public IReadOnlyList<SandboxSessionSnapshot> CollectIdle(TimeSpan timeout) => [];

        public int ActiveSessionCount => 0;
    }
}
