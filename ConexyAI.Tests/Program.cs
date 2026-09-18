using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

int failures = 0;

await RunAsync("UpdateAsync avoids ChangeTracker conflict when entity is already tracked", TestUpdateAsyncAvoidsTrackingConflictAsync);
await RunAsync("ExecuteAsync reuses an owned session and updates in place", TestExecuteAsyncReuseOwnedSessionAsync);
await RunAsync("ExecuteAsync never reuses another user's session id", TestExecuteAsyncForeignSessionAsync);
await RunAsync("Two parallel updates of the same id do not throw a tracking conflict", TestParallelUpdateSameIdAsync);
await RunAsync("CreateOrGetAsync is idempotent under a parallel creation race (Postgres)", TestCreateOrGetAsyncIsIdempotentUnderParallelRaceAsync);
await RunAsync("QueueGuard deduplicates parallel enqueue for the same session", TestQueueGuardDeduplicatesParallelEnqueueAsync);
await RunAsync("QueueGuard releases the guard after completion (no leak)", TestQueueGuardReleasesAfterCompletionAsync);
await RunAsync("str_replace_editor: no match -> no_match", TestEditorStrReplaceNoMatchAsync);
await RunAsync("str_replace_editor: ambiguous match leaves file unchanged", TestEditorStrReplaceAmbiguousAsync);
await RunAsync("str_replace_editor: unique match + undo", TestEditorStrReplaceSuccessAndUndoAsync);
await RunAsync("str_replace_editor: path traversal is blocked", TestEditorPathTraversalBlockedAsync);
await RunAsync("str_replace_editor: large file view is truncated", TestEditorLargeFileViewAsync);
await RunAsync("str_replace_editor: create -> ambiguous -> refine -> undo chain", TestEditorCreateReplaceUndoChainAsync);
await RunAsync("bash: >80 lines output is truncated", TestBashTruncationAsync);
await RunAsync("bash: timeout kills the process", TestBashTimeoutAsync);
await RunAsync("bash: process tree is killed on timeout", TestBashKillsProcessTreeAsync);
await RunAsync("bash: workspace_not_found for a missing session", TestBashWorkspaceNotFoundAsync);
await RunAsync("bash: runs a real shell command", TestBashRunsRealCommandAsync);
await RunAsync("bash: runs dotnet --version", TestBashRunsDotnetAsync);
await RunAsync("bash: coreutils (ls/rm/cat) resolve via Git bash PATH", TestBashFindsCoreutilsAsync);
await RunAsync("todo_write: validation errors", TestTodoValidationErrorsAsync);
await RunAsync("todo_write: full replacement", TestTodoFullReplacementAsync);
await RunAsync("todo_write: clear on session end", TestTodoClearAsync);
await RunAsync("web_search: JSON SEO response is parsed into results", TestJsonSeoSearchParsingAsync);
await RunAsync("llm: non-streaming message.content is extracted as text", TestNonStreamingMessageContentExtractionAsync);
await RunAsync("ide files: create -> content -> save -> rename -> delete", TestIdeFileCrudAsync);
await RunAsync("ide files: path traversal is blocked (shared validator)", TestIdeFilePathTraversalBlockedAsync);
await RunAsync("ide files: manual save invalidates agent undo stack", TestManualSaveInvalidatesEditorUndoAsync);
await RunOrSkipAsync(
    "ide terminal: start reuses pty and streams output",
    TestTerminalReuseAndOutputAsync,
    skip: OperatingSystem.IsWindows(),
    skipReason: "requires Linux pty path, run in CI/Linux");

if (failures == 0)
{
    Console.WriteLine("\n=== ALL TESTS PASSED ===");
    return 0;
}

Console.WriteLine($"\n=== {failures} TEST(S) FAILED ===");
return 1;

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
    }
}

async Task RunOrSkipAsync(string name, Func<Task> test, bool skip, string skipReason)
{
    if (skip)
    {
        Console.WriteLine($"SKIP  {name}");
        Console.WriteLine($"      {skipReason}");
        return;
    }

    await RunAsync(name, test);
}

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Assertion failed: {message}");
}

DbConexy CreateContext(string dbName)
{
    var options = new DbContextOptionsBuilder<DbConexy>()
        .UseInMemoryDatabase(dbName)
        .Options;
    return new DbConexy(options);
}

(ConexyService Service, FakeQueue Queue) CreateService(DbConexy context)
{
    var repo = new ConexyRepository(context);
    var queue = new FakeQueue();
    var guard = new ConexyQueueGuard(queue, NullLogger<ConexyQueueGuard>.Instance);
    var env = new FakeWebHostEnvironment { EnvironmentName = "Development" };
    return (new ConexyService(repo, guard, env), queue);
}

string AdminConnectionString() => "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";
string TestConnectionString(string dbName) => $"Host=localhost;Port=5433;Database={dbName};Username=postgres;Password=postgres";

DbConexy CreateNpgsqlContext(string dbName) =>
    new(new DbContextOptionsBuilder<DbConexy>().UseNpgsql(TestConnectionString(dbName)).Options);

string CreateTempWorkspaceRoot()
{
    var path = Path.Combine(Path.GetTempPath(), "conexy_editor_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

// On Windows/MSYS2, a spawned bash process (and its short-lived helper) can hold the
// working-directory handle for a brief moment after exit, so retry deletion.
void DeleteDirBestEffort(string path)
{
    for (var i = 0; i < 15; i++)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            return;
        }
        catch (IOException) { Thread.Sleep(100); }
        catch (UnauthorizedAccessException) { Thread.Sleep(100); }
    }
}

ConexyEditorService CreateEditorService(string rootPath)
{
    var workspace = new ConexyWorkspaceService(
        Options.Create(new WorkspaceOptions { RootPath = rootPath }),
        NullLogger<ConexyWorkspaceService>.Instance);
    var hub = new FakeHubContext();
    var validator = new WorkspacePathValidator(workspace);
    var state = new ConexyEditorStateService();
    return new ConexyEditorService(workspace, state, validator, hub, NullLogger<ConexyEditorService>.Instance);
}

ConexyWorkspaceService CreateWorkspaceService(string rootPath) =>
    new(
        Options.Create(new WorkspaceOptions { RootPath = rootPath }),
        NullLogger<ConexyWorkspaceService>.Instance);

IdeFileService CreateFileService(
    ConexyWorkspaceService workspace,
    ConexyEditorStateService state,
    IHubContext<ConexyHub>? hub = null)
{
    var validator = new WorkspacePathValidator(workspace);
    return new IdeFileService(
        workspace,
        validator,
        state,
        hub ?? new FakeHubContext(),
        NullLogger<IdeFileService>.Instance);
}

ConexyBashService CreateBashService(string rootPath)
{
    var workspace = new ConexyWorkspaceService(
        Options.Create(new WorkspaceOptions { RootPath = rootPath }),
        NullLogger<ConexyWorkspaceService>.Instance);
    var hub = new FakeHubContext();
    return new ConexyBashService(workspace, hub, NullLogger<ConexyBashService>.Instance);
}

ConexyTodoService CreateTodoService(RecordingHubContext hub) =>
    new(hub, NullLogger<ConexyTodoService>.Instance);

// Reproduces the original read-then-write bug: an entity is already tracked, then a
// *different* instance with the same key is passed to update. UpdateAsync must use the
// Local instance instead of attaching a duplicate and throwing "cannot be tracked".
async Task TestUpdateAsyncAvoidsTrackingConflictAsync()
{
    var dbName = Guid.NewGuid().ToString("N");
    var id = Guid.NewGuid();
    var userId = Guid.NewGuid();

    await using (var seed = CreateContext(dbName))
    {
        seed.Conexy.Add(new ConexyEntity { Id = id, UserId = userId, Model = "ConexyV1-flash", Prompt = "initial", Status = ConexyStatus.Completed });
        await seed.SaveChangesAsync();
    }

    await using var context = CreateContext(dbName);
    var repo = new ConexyRepository(context);

    // Simulate a tracked read (this is what GetByIdAsync used to do).
    var tracked = await context.Conexy.FirstAsync(e => e.Id == id);
    Assert(tracked.Status == ConexyStatus.Completed, "seed status should be Completed");

    // Update through a detached instance sharing the same key.
    var detached = new ConexyEntity { Id = id, UserId = userId, Model = "ConexyV1-flash", Prompt = "updated", Status = ConexyStatus.Running };
    await repo.UpdateAsync(detached);

    var result = await context.Conexy.AsNoTracking().FirstAsync(e => e.Id == id);
    Assert(result.Prompt == "updated", "prompt should be updated");
    Assert(result.Status == ConexyStatus.Running, "status should be updated");
}

async Task TestExecuteAsyncReuseOwnedSessionAsync()
{
    var dbName = Guid.NewGuid().ToString("N");
    var sessionId = Guid.NewGuid();
    var userId = Guid.NewGuid();

    await using (var seed = CreateContext(dbName))
    {
        seed.Conexy.Add(new ConexyEntity { Id = sessionId, UserId = userId, Model = "ConexyV1-flash", Prompt = "first", Status = ConexyStatus.Completed });
        await seed.SaveChangesAsync();
    }

    await using var context = CreateContext(dbName);
    var (service, _) = CreateService(context);

    var response = await service.ExecuteAsync(userId, new ConexyRequest("ConexyV1-flash", "second", SessionId: sessionId.ToString()));
    Assert(response.Id == sessionId, "owned session should reuse the same task id");
    Assert(response.Prompt == "second", "response should carry the updated prompt");

    var stored = await context.Conexy.AsNoTracking().FirstAsync(e => e.Id == sessionId);
    Assert(stored.Prompt == "second", "stored prompt should be updated");
    Assert(stored.Status == ConexyStatus.Pending, "stored status should be reset to Pending");
}

async Task TestExecuteAsyncForeignSessionAsync()
{
    var dbName = Guid.NewGuid().ToString("N");
    var sessionId = Guid.NewGuid();
    var ownerId = Guid.NewGuid();
    var otherId = Guid.NewGuid();

    await using (var seed = CreateContext(dbName))
    {
        seed.Conexy.Add(new ConexyEntity { Id = sessionId, UserId = ownerId, Model = "ConexyV1-flash", Prompt = "owner prompt", Status = ConexyStatus.Completed });
        await seed.SaveChangesAsync();
    }

    await using var context = CreateContext(dbName);
    var (service, queue) = CreateService(context);

    var response = await service.ExecuteAsync(otherId, new ConexyRequest("ConexyV1-flash", "other prompt", SessionId: sessionId.ToString()));
    Assert(response.Id != sessionId, "foreign session must NOT reuse the owner's task id");
    Assert(queue.Enqueued.Count == 1, "a new job should be enqueued");
}

async Task TestParallelUpdateSameIdAsync()
{
    var dbName = Guid.NewGuid().ToString("N");
    var sessionId = Guid.NewGuid();
    var userId = Guid.NewGuid();

    await using (var seed = CreateContext(dbName))
    {
        seed.Conexy.Add(new ConexyEntity { Id = sessionId, UserId = userId, Model = "ConexyV1-flash", Prompt = "initial", Status = ConexyStatus.Completed });
        await seed.SaveChangesAsync();
    }

    await using var ctx1 = CreateContext(dbName);
    await using var ctx2 = CreateContext(dbName);
    var (s1, _) = CreateService(ctx1);
    var (s2, _) = CreateService(ctx2);

    var req1 = new ConexyRequest("ConexyV1-flash", "update-1", SessionId: sessionId.ToString());
    var req2 = new ConexyRequest("ConexyV1-flash", "update-2", SessionId: sessionId.ToString());

    try
    {
        await Task.WhenAll(s1.ExecuteAsync(userId, req1), s2.ExecuteAsync(userId, req2));
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be tracked"))
    {
        Assert(false, $"parallel updates must not throw a ChangeTracker conflict: {ex.Message}");
    }
}

// Uses a real PostgreSQL database to exercise the provider-specific unique-violation
// path (SqlState 23505) that the InMemory provider cannot reproduce.
async Task TestCreateOrGetAsyncIsIdempotentUnderParallelRaceAsync()
{
    var sessionId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    var dbName = "conexy_it_" + Guid.NewGuid().ToString("N");

    try
    {
        await using (var admin = new NpgsqlConnection(AdminConnectionString()))
        {
            await admin.OpenAsync();
            await using var createCmd = admin.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE {dbName}";
            await createCmd.ExecuteNonQueryAsync();
        }

        await using (var schema = CreateNpgsqlContext(dbName))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await using var ctx1 = CreateNpgsqlContext(dbName);
        await using var ctx2 = CreateNpgsqlContext(dbName);
        var repo1 = new ConexyRepository(ctx1);
        var repo2 = new ConexyRepository(ctx2);

        var e1 = new ConexyEntity { Id = sessionId, UserId = userId, Model = "ConexyV1-flash", Prompt = "prompt", Status = ConexyStatus.Pending };
        var e2 = new ConexyEntity { Id = sessionId, UserId = userId, Model = "ConexyV1-flash", Prompt = "prompt", Status = ConexyStatus.Pending };

        var results = await Task.WhenAll(repo1.CreateOrGetAsync(e1), repo2.CreateOrGetAsync(e2));

        Assert(results[0].Id == sessionId, "first result should carry the requested session id");
        Assert(results[1].Id == sessionId, "second result should carry the requested session id");

        await using var verify = CreateNpgsqlContext(dbName);
        var count = await verify.Conexy.CountAsync(x => x.Id == sessionId);
        Assert(count == 1, $"expected exactly one row for the session, found {count}");
    }
    finally
    {
        try
        {
            await using var admin = new NpgsqlConnection(AdminConnectionString());
            await admin.OpenAsync();
            await using var dropCmd = admin.CreateCommand();
            dropCmd.CommandText = $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)";
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup; never mask the original test result.
        }
    }
}

async Task TestQueueGuardDeduplicatesParallelEnqueueAsync()
{
    var queue = new ConexyQueue();
    var guard = new ConexyQueueGuard(queue, NullLogger<ConexyQueueGuard>.Instance);

    var taskId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    var job = new ConexyJob(taskId, taskId, userId, ConexyModelType.ConexyCoder, "prompt");

    var results = await Task.WhenAll(
        guard.EnqueueIfNotInFlightAsync(job),
        guard.EnqueueIfNotInFlightAsync(job));

    var enqueued = results.Count(r => r);
    Assert(enqueued == 1, $"expected exactly one enqueue, got {enqueued}");
}

async Task TestQueueGuardReleasesAfterCompletionAsync()
{
    var queue = new ConexyQueue();
    var guard = new ConexyQueueGuard(queue, NullLogger<ConexyQueueGuard>.Instance);

    var taskId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    var job = new ConexyJob(taskId, taskId, userId, ConexyModelType.ConexyCoder, "prompt");

    var first = await guard.EnqueueIfNotInFlightAsync(job);
    Assert(first, "first enqueue should succeed");

    // Simulate the worker completing the task.
    guard.MarkCompleted(taskId);

    var second = await guard.EnqueueIfNotInFlightAsync(job);
    Assert(second, "after completion the same session should enqueue again (no guard leak)");
}

async Task TestEditorStrReplaceNoMatchAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var editor = CreateEditorService(root);
        var path = "test.cs";
        await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "create", Path = path, FileText = "hello world\n" });

        var res = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "str_replace", Path = path, OldStr = "nonexistent", NewStr = "x" });
        Assert(!res.Success && res.ErrorType == "no_match", $"expected no_match, got {res.ErrorType}");
    }
    finally { Directory.Delete(root, true); }
}

async Task TestEditorStrReplaceAmbiguousAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var editor = CreateEditorService(root);
        var path = "test.cs";
        var original = "foo\nfoo\n";
        await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "create", Path = path, FileText = original });

        var res = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "str_replace", Path = path, OldStr = "foo", NewStr = "bar" });
        Assert(!res.Success && res.ErrorType == "ambiguous_match", $"expected ambiguous_match, got {res.ErrorType}");

        var content = File.ReadAllText(Path.Combine(root, taskId.ToString("N"), path));
        Assert(content == original, "file must be unchanged after ambiguous match");
    }
    finally { Directory.Delete(root, true); }
}

async Task TestEditorStrReplaceSuccessAndUndoAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var editor = CreateEditorService(root);
        var path = "test.cs";
        await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "create", Path = path, FileText = "aaa foo bbb\n" });

        var res = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "str_replace", Path = path, OldStr = "foo", NewStr = "bar" });
        Assert(res.Success, "unique replacement should succeed");
        var content = File.ReadAllText(Path.Combine(root, taskId.ToString("N"), path));
        Assert(content == "aaa bar bbb\n", "file should contain the replacement");

        var undo = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "undo", Path = path });
        Assert(undo.Success, "undo should succeed");
        var reverted = File.ReadAllText(Path.Combine(root, taskId.ToString("N"), path));
        Assert(reverted == "aaa foo bbb\n", "file should be reverted after undo");
    }
    finally { Directory.Delete(root, true); }
}

async Task TestEditorPathTraversalBlockedAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var editor = CreateEditorService(root);
        var res = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "view", Path = "../outside.txt" });
        Assert(!res.Success && res.ErrorType == "io_error", $"expected io_error for traversal, got {res.ErrorType}");
    }
    finally { Directory.Delete(root, true); }
}

async Task TestEditorLargeFileViewAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var editor = CreateEditorService(root);
        var path = "big.txt";
        var big = new string('A', 600 * 1024);
        await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "create", Path = path, FileText = big });

        var res = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "view", Path = path });
        Assert(res.Success, "view should succeed (not crash)");
        Assert(res.Output != null && res.Output.Length < big.Length, "output should be truncated for a large file");
    }
    finally { Directory.Delete(root, true); }
}

async Task TestEditorCreateReplaceUndoChainAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var editor = CreateEditorService(root);
        var path = "app.cs";
        var initial = "line1\nfoo\nline3\nfoo\nline5\n";

        var create = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "create", Path = path, FileText = initial });
        Assert(create.Success, "create should succeed");

        var amb = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "str_replace", Path = path, OldStr = "foo", NewStr = "bar" });
        Assert(!amb.Success && amb.ErrorType == "ambiguous_match", $"expected ambiguous_match, got {amb.ErrorType}");

        var ok = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "str_replace", Path = path, OldStr = "line3\nfoo", NewStr = "line3\nbar" });
        Assert(ok.Success, "refined str_replace should succeed");
        var after = File.ReadAllText(Path.Combine(root, taskId.ToString("N"), path));
        Assert(after == "line1\nfoo\nline3\nbar\nline5\n", "refined replacement should apply");

        var undo = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "undo", Path = path });
        Assert(undo.Success, "undo should succeed");
        var reverted = File.ReadAllText(Path.Combine(root, taskId.ToString("N"), path));
        Assert(reverted == initial, "undo should revert to the created content");
    }
    finally { Directory.Delete(root, true); }
}

async Task TestBashTruncationAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var bash = CreateBashService(root);
        Directory.CreateDirectory(Path.Combine(root, taskId.ToString("N")));

        var res = await bash.ExecuteAsync(taskId, new BashToolRequest { Command = "for i in $(seq 1 200); do echo \"line $i\"; done" });
        Assert(res.Success, "bash command should succeed");
        Assert(res.WasTruncated, "output longer than 80 lines should be truncated");
        Assert(res.Output.Contains("line 1") && res.Output.Contains("line 40"), "head should contain the first lines");
        Assert(res.Output.Contains("truncated"), "output should contain a truncation marker");
        Assert(res.Output.Contains("line 200"), "tail should contain the last line");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestBashTimeoutAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var bash = CreateBashService(root);
        Directory.CreateDirectory(Path.Combine(root, taskId.ToString("N")));

        var res = await bash.ExecuteAsync(taskId, new BashToolRequest { Command = "sleep 5", TimeoutSeconds = 1 });
        Assert(!res.Success && res.WasTimedOut && res.ErrorType == "timeout", $"expected timeout, got Success={res.Success} ErrorType={res.ErrorType}");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestBashKillsProcessTreeAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var bash = CreateBashService(root);
        Directory.CreateDirectory(Path.Combine(root, taskId.ToString("N")));

        // A backgrounded child (`sleep 10`) survives a plain parent kill; Kill(entireProcessTree:true) must reap it too.
        var res = await bash.ExecuteAsync(taskId, new BashToolRequest { Command = "sleep 10 & wait", TimeoutSeconds = 2 });
        Assert(res.WasTimedOut && res.ErrorType == "timeout", "command with a background child should time out");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestBashWorkspaceNotFoundAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid(); // workspace dir is never created
    try
    {
        var bash = CreateBashService(root);
        var res = await bash.ExecuteAsync(taskId, new BashToolRequest { Command = "echo hi" });
        Assert(!res.Success && res.ErrorType == "workspace_not_found", $"expected workspace_not_found, got {res.ErrorType}");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestBashRunsRealCommandAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var bash = CreateBashService(root);
        Directory.CreateDirectory(Path.Combine(root, taskId.ToString("N")));

        var res = await bash.ExecuteAsync(taskId, new BashToolRequest { Command = "echo conexy-bash-ok" });
        Assert(res.Success && res.ExitCode == 0, $"echo should succeed, exit={res.ExitCode}");
        Assert(res.Output.Contains("conexy-bash-ok"), "output should contain the echoed text");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestBashRunsDotnetAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var bash = CreateBashService(root);
        Directory.CreateDirectory(Path.Combine(root, taskId.ToString("N")));

        var res = await bash.ExecuteAsync(taskId, new BashToolRequest { Command = "dotnet --version" });
        Assert(res.Success && res.ExitCode == 0, $"dotnet --version should succeed, exit={res.ExitCode}");
        Assert(!string.IsNullOrWhiteSpace(res.Output), "dotnet --version should produce output");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestBashFindsCoreutilsAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    try
    {
        var bash = CreateBashService(root);
        Directory.CreateDirectory(Path.Combine(root, taskId.ToString("N")));

        // The agent bash tool runs via Git for Windows' bash on dev machines. Its PATH must
        // include Git\usr\bin so coreutils (ls/rm/cat) resolve instead of falling back to a
        // different shell. This is the regression guard for the coreutils PATH fix.
        var res = await bash.ExecuteAsync(taskId, new BashToolRequest { Command = "command -v ls && command -v rm && command -v cat" });
        Assert(res.Success && res.ExitCode == 0, $"coreutils should be found, exit={res.ExitCode} output={res.Output}");
        Assert(res.Output.Contains("ls") && res.Output.Contains("rm") && res.Output.Contains("cat"), "coreutils ls/rm/cat should resolve to real paths");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestTodoValidationErrorsAsync()
{
    var hub = new RecordingHubContext();
    var service = CreateTodoService(hub);
    var sessionId = Guid.NewGuid();

    var badStatus = await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "1", Content = "a", Status = "bogus" } } });
    Assert(!badStatus.Success && badStatus.ErrorType == "invalid_status", $"expected invalid_status, got {badStatus.ErrorType}");

    var noSkipReason = await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "1", Content = "a", Status = "skipped" } } });
    Assert(!noSkipReason.Success && noSkipReason.ErrorType == "missing_skip_reason", $"expected missing_skip_reason, got {noSkipReason.ErrorType}");

    var dup = await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "1", Content = "a", Status = "pending" }, new() { Id = "1", Content = "b", Status = "pending" } } });
    Assert(!dup.Success && dup.ErrorType == "duplicate_id", $"expected duplicate_id, got {dup.ErrorType}");

    var twoInProgress = await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "1", Content = "a", Status = "in_progress" }, new() { Id = "2", Content = "b", Status = "in_progress" } } });
    Assert(!twoInProgress.Success && twoInProgress.ErrorType == "invalid_status", $"expected invalid_status for two in_progress, got {twoInProgress.ErrorType}");
}

async Task TestTodoFullReplacementAsync()
{
    var hub = new RecordingHubContext();
    var service = CreateTodoService(hub);
    var sessionId = Guid.NewGuid();

    await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "1", Content = "a", Status = "pending" } } });
    await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "2", Content = "b", Status = "completed" } } });

    var updates = hub.Sent.Where(s => s.Method == "TodoUpdate").ToList();
    Assert(updates.Count == 2, $"expected two TodoUpdate events, got {updates.Count}");
    var last = (TodoUpdateEvent)updates[^1].Args[0]!;
    Assert(last.Todos.Count == 1 && last.Todos[0].Id == "2", "second call should fully replace the first list");
}

async Task TestTodoClearAsync()
{
    var hub = new RecordingHubContext();
    var service = CreateTodoService(hub);
    var sessionId = Guid.NewGuid();

    await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "1", Content = "a", Status = "pending" } } });

    // Clear must be idempotent and must not throw.
    service.Clear(sessionId);
    service.Clear(sessionId);

    var res = await service.WriteAsync(sessionId, new TodoWriteRequest { Todos = new List<TodoItem> { new() { Id = "2", Content = "b", Status = "in_progress" } } });
    Assert(res.Success, "write after clear should succeed");
    var updates = hub.Sent.Where(s => s.Method == "TodoUpdate").ToList();
    Assert(updates.Count == 2, $"expected one event before clear and one after, got {updates.Count}");
}

async Task TestIdeFileCrudAsync()
{
    var root = CreateTempWorkspaceRoot();
    var sessionId = Guid.NewGuid();
    var workspace = CreateWorkspaceService(root);
    var state = new ConexyEditorStateService();
    var hub = new RecordingHubContext();
    var files = CreateFileService(workspace, state, hub);

    try
    {
        // create
        await files.CreateAsync(sessionId, "src/app.cs", isDirectory: false);
        var created = Path.Combine(root, sessionId.ToString("N"), "src", "app.cs");
        Assert(File.Exists(created), "create should write the file on disk");

        // content (empty after create)
        var initial = await files.GetContentAsync(sessionId, "src/app.cs");
        Assert(!initial.IsBinary && initial.Content == string.Empty, "freshly created file should be empty text");

        // save
        await files.SaveAsync(sessionId, "src/app.cs", "hello world");
        var saved = await files.GetContentAsync(sessionId, "src/app.cs");
        Assert(saved.Content == "hello world", "save should persist content");

        // rename
        await files.RenameAsync(sessionId, "src/app.cs", "src/main.cs");
        Assert(!File.Exists(created), "old path should be gone after rename");
        Assert(File.Exists(Path.Combine(root, sessionId.ToString("N"), "src", "main.cs")), "new path should exist after rename");

        // delete
        await files.DeleteAsync(sessionId, "src/main.cs");
        Assert(!File.Exists(Path.Combine(root, sessionId.ToString("N"), "src", "main.cs")), "delete should remove the file");

        // every mutation should have broadcast a ToolAction with ToolName = user
        var userActions = hub.Sent.Where(s => s.Method == "ToolAction" && ((ToolActionEvent)s.Args[0]!).ToolName == "user").ToList();
        Assert(userActions.Count >= 4, $"expected at least 4 user tool actions, got {userActions.Count}");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestIdeFilePathTraversalBlockedAsync()
{
    var root = CreateTempWorkspaceRoot();
    var sessionId = Guid.NewGuid();
    var workspace = CreateWorkspaceService(root);
    var state = new ConexyEditorStateService();
    var files = CreateFileService(workspace, state);

    try
    {
        var caught = false;
        try
        {
            await files.GetContentAsync(sessionId, "../outside.txt");
        }
        catch (UnauthorizedAccessException)
        {
            caught = true;
        }
        Assert(caught, "path traversal must be rejected by the shared validator");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestManualSaveInvalidatesEditorUndoAsync()
{
    var root = CreateTempWorkspaceRoot();
    var taskId = Guid.NewGuid();
    var workspace = CreateWorkspaceService(root);
    var state = new ConexyEditorStateService();
    var validator = new WorkspacePathValidator(workspace);
    var editor = new ConexyEditorService(workspace, state, validator, new FakeHubContext(), NullLogger<ConexyEditorService>.Instance);
    var files = CreateFileService(workspace, state);

    try
    {
        var path = "app.cs";
        await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "create", Path = path, FileText = "foo\n" });
        var replace = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "str_replace", Path = path, OldStr = "foo", NewStr = "bar" });
        Assert(replace.Success, "str_replace should succeed and push an undo snapshot");

        // A manual save through the REST service must invalidate the agent's undo stack.
        await files.SaveAsync(taskId, path, "manual edit");

        var undo = await editor.ExecuteAsync(taskId, new StrReplaceEditorRequest { Command = "undo", Path = path });
        Assert(!undo.Success && undo.ErrorType == "io_error", "undo must be invalidated after a manual save");
    }
    finally { DeleteDirBestEffort(root); }
}

async Task TestTerminalReuseAndOutputAsync()
{
    var root = CreateTempWorkspaceRoot();
    var sessionId = Guid.NewGuid();
    var workspace = CreateWorkspaceService(root);
    var hub = new RecordingHubContext();
    var terminal = new IdeTerminalService(workspace, hub, NullLogger<IdeTerminalService>.Instance);

    try
    {
        await terminal.StartAsync(sessionId);
        Assert(terminal.ActiveSessionCount == 1, "one pty session should exist after start");

        await terminal.StartAsync(sessionId);
        Assert(terminal.ActiveSessionCount == 1, "a repeated start must reuse the existing pty, not spawn a second one");

        var marker = "CONEXY_PTY_OK_" + Guid.NewGuid().ToString("N");
        await terminal.SendInputAsync(sessionId, "echo " + marker + Environment.NewLine);

        var received = await WaitForTerminalOutputAsync(hub, marker, TimeSpan.FromSeconds(15));
        Assert(received, "terminal output should contain the echoed marker");

        await terminal.StopAsync(sessionId);
        Assert(terminal.ActiveSessionCount == 0, "stop should release the pty session");
    }
    finally
    {
        terminal.StopAll();
        DeleteDirBestEffort(root);
    }
}

async Task<bool> WaitForTerminalOutputAsync(RecordingHubContext hub, string marker, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var output = string.Concat(
            hub.Sent
                .Where(s => s.Method == "TerminalOutput")
                .Select(s => ((TerminalOutputEvent)s.Args[0]!).Data));
        if (output.Contains(marker, StringComparison.Ordinal))
            return true;
        await Task.Delay(100);
    }
    return false;
}

async Task TestJsonSeoSearchParsingAsync()
{
    var json = """
        {
            "pages": 1,
            "exhausted": false,
            "found": 116000000,
            "found_human": "нашлось 116 млн результатов",
            "lr": 213,
            "query": "купить ноутбук",
            "results": [
                {
                    "url": "https://www.example.ru/category/noutbuki/",
                    "domain": "www.example.ru",
                    "title": "Ноутбуки: купить ноутбук по низкой цене",
                    "passage": "Покупайте ноутбуки по выгодным ценам...",
                    "breadcrumbs": "example.ru›Электроника›Ноутбуки"
                }
            ]
        }
        """;

    var handler = new FakeHttpMessageHandler
    {
        Responder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }
    };

    var service = new JsonSeoSearchService(
        new HttpClient(handler),
        Options.Create(new WebSearchOptions { JsonSeoApiKey = "test-key" }),
        NullLogger<JsonSeoSearchService>.Instance);

    var result = await service.SearchAsync("купить ноутбук");

    Assert(result.Success, "a 200 with results must succeed");
    Assert(result.Output.Contains("Ноутбуки: купить ноутбук по низкой цене", StringComparison.Ordinal), "output must include the result title");
    Assert(result.Output.Contains("https://www.example.ru/category/noutbuki/", StringComparison.Ordinal), "output must include the result URL");
    Assert(result.Output.Contains("Покупайте ноутбуки по выгодным ценам", StringComparison.Ordinal), "output must include the passage");

    Assert(handler.LastRequest is not null, "a request must have been sent");
    Assert(handler.LastRequest!.Headers.Authorization?.Scheme == "Bearer", "Authorization must use Bearer scheme");
    Assert(handler.LastRequest.Headers.Authorization!.Parameter == "test-key", "Authorization must carry the key");
    Assert(handler.LastRequest.RequestUri!.Query.Contains("text=" + Uri.EscapeDataString("купить ноутбук"), StringComparison.Ordinal), "the query must be URL-encoded into the text parameter");
}

Task TestNonStreamingMessageContentExtractionAsync()
{
    // The non-streaming chat.completion response carries the answer in choices[0].message.content.
    // Regression: content is deserialized into the object-typed Content as a JsonElement, and a
    // bare `Content as string` cast silently returned null (empty chat answer in the search path).
    var json = """{"choices":[{"message":{"role":"assistant","content":"iPhone Duo не существует"},"finish_reason":"stop"}]}""";
    var result = System.Text.Json.JsonSerializer.Deserialize<LlmChatResponse>(json);

    Assert(result is not null, "response should deserialize");
    Assert(result!.Choices.Count == 1, "one choice expected");
    Assert(result.Choices[0].Message.Text == "iPhone Duo не существует", "Text must extract the non-streaming message.content");
    return Task.CompletedTask;
}

sealed class FakeQueue : IConexyQueue
{
    public List<ConexyJob> Enqueued { get; } = new();

    public ValueTask EnqueueAsync(ConexyJob job, CancellationToken ct = default)
    {
        Enqueued.Add(job);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ConexyJob> ReadAllAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();
}

sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Test";
    public IFileProvider WebRootFileProvider { get; set; } = null!;
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}

sealed class FakeHubContext : IHubContext<ConexyHub>
{
    public IHubClients Clients { get; } = new FakeHubClients();
    public IGroupManager Groups { get; } = new FakeGroupManager();
}

sealed class FakeHubClients : IHubClients
{
    public IClientProxy All => NoopClientProxy.Instance;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NoopClientProxy.Instance;
    public IClientProxy Client(string connectionId) => NoopClientProxy.Instance;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => NoopClientProxy.Instance;
    public IClientProxy Group(string groupName) => NoopClientProxy.Instance;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NoopClientProxy.Instance;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => NoopClientProxy.Instance;
    public IClientProxy User(string userId) => NoopClientProxy.Instance;
    public IClientProxy Users(IReadOnlyList<string> userIds) => NoopClientProxy.Instance;
}

sealed class NoopClientProxy : IClientProxy
{
    public static NoopClientProxy Instance { get; } = new();
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class FakeGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class RecordingHubContext : IHubContext<ConexyHub>
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Method, object?[] Args)> Sent { get; } = new();
    public IHubClients Clients { get; }
    public IGroupManager Groups { get; } = new FakeGroupManager();

    public RecordingHubContext()
    {
        Clients = new RecordingHubClients(this);
    }
}

sealed class RecordingHubClients : IHubClients
{
    private readonly RecordingHubContext _hub;
    public RecordingHubClients(RecordingHubContext hub) { _hub = hub; }

    public IClientProxy All => new RecordingClientProxy(_hub);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_hub);
    public IClientProxy Client(string connectionId) => new RecordingClientProxy(_hub);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingClientProxy(_hub);
    public IClientProxy Group(string groupName) => new RecordingClientProxy(_hub);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_hub);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingClientProxy(_hub);
    public IClientProxy User(string userId) => new RecordingClientProxy(_hub);
    public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingClientProxy(_hub);
}

sealed class RecordingClientProxy : IClientProxy
{
    private readonly RecordingHubContext _hub;
    public RecordingClientProxy(RecordingHubContext hub) { _hub = hub; }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        _hub.Sent.Enqueue((method, args));
        return Task.CompletedTask;
    }
}

sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ =>
        new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Responder(request));
    }
}
