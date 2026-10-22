using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Model;
using ConexyAI.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// AGENT_EVAL: добавлено 2026-10-18 — стенд, который гоняет НАСТОЯЩУЮ модель через НАСТОЯЩИЙ ConexyAgentRunner
// на небольших проектах и проверяет результат автоматически (тесты проекта + точность правок).
//
//   dotnet run --project ConexyAI.Tests -- eval --selfcheck        проверить задачи и проверяющий код (без ключа, бесплатно)
//   dotnet run --project ConexyAI.Tests -- eval --list
//   dotnet run --project ConexyAI.Tests -- eval [--only id,id] [--runs 3] [--no-audit] [--out dir]
//
// Ключ берётся из AiUpstream:DeepSeek:ApiKey (appsettings.json) или переменной AiUpstream__DeepSeek__ApiKey.
// bash агента исполняется НА ХОСТЕ во временной папке (Docker-песочницы здесь нет) — см. EvalHostBash:
// это защита от случайностей, а не настоящая изоляция.
internal static class AgentEval
{
    // ------------------------------------------------------------------ entry point

    public static async Task<int> RunAsync(string[] args)
    {
        var opts = EvalOptions.Parse(args);
        var tasks = EvalTasks.All
            .Where(t => opts.Only.Count == 0 || opts.Only.Contains(t.Id))
            .ToList();

        if (opts.List)
        {
            foreach (var t in tasks) Console.WriteLine($"{t.Id,-28} {t.Title}");
            return 0;
        }
        // ADHOC: прогон на настоящем проекте снаружи (——dir/——prompt). Задачи eval для него не нужны.
        if (opts.Dir is { Length: > 0 })
            return await RunAdHocAsync(opts);
        if (tasks.Count == 0)
        {
            Console.Error.WriteLine("Нет задач под фильтр --only.");
            return 2;
        }
        return opts.SelfCheck ? await SelfCheckAsync(tasks) : await RunLiveAsync(tasks, opts);
    }

    // ------------------------------------------------------------------ self-check (no API key)

    private static async Task<int> SelfCheckAsync(List<EvalTask> tasks)
    {
        var bad = 0;
        foreach (var task in tasks)
        {
            var dir = NewTempDir("selfcheck-" + task.Id);
            try
            {
                var before = Materialize(task, dir);
                var baseline = await task.Verify(new EvalCtx(dir, before));
                if (baseline.Count == 0)
                {
                    bad++;
                    Console.WriteLine($"FAIL {task.Id}: исходный проект УЖЕ проходит проверку (проверка ничего не ловит)");
                    continue;
                }

                task.Solve(dir);
                var after = Snapshot(dir);
                var changes = Diff(before, after);
                var failures = await task.Verify(new EvalCtx(dir, before));
                var precision = Precision(task, changes);
                if (failures.Count > 0)
                {
                    bad++;
                    Console.WriteLine($"FAIL {task.Id}: эталонное решение не проходит проверку:");
                    foreach (var f in failures) Console.WriteLine("       - " + f);
                }
                else if (precision.Count > 0)
                {
                    bad++;
                    Console.WriteLine($"FAIL {task.Id}: эталонное решение нарушает бюджет точности: {string.Join("; ", precision)}");
                }
                else
                {
                    Console.WriteLine($"ok   {task.Id}: baseline красный ({baseline.Count} замечаний), эталон зелёный, изменено строк: {changes.Sum(c => c.Lines)}");
                }
            }
            finally
            {
                TryDelete(dir);
            }
        }
        Console.WriteLine(bad == 0 ? "\nSELFCHECK OK" : $"\nSELFCHECK: проблем — {bad}");
        return bad == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ live run

    private static async Task<int> RunLiveAsync(List<EvalTask> tasks, EvalOptions opts)
    {
        var repoRoot = FindRepoRoot();
        var ai = LoadUpstream(repoRoot);
        if (string.IsNullOrWhiteSpace(ai.DeepSeek.ApiKey))
        {
            Console.Error.WriteLine("Ключ не задан: AiUpstream:DeepSeek:ApiKey в ConexyAI/appsettings.json или переменная AiUpstream__DeepSeek__ApiKey.");
            return 2;
        }

        var outDir = opts.Out ?? Path.Combine(repoRoot, "eval-results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "transcripts"));

        Console.WriteLine($"Модель (upstream): {ai.DeepSeek.FlashModel} · задач: {tasks.Count} · прогонов на задачу: {opts.Runs} · аудит: {(opts.NoAudit ? "выкл" : "вкл")}");
        Console.WriteLine($"Результаты: {outDir}\n");

        var results = new List<EvalRun>();
        foreach (var task in tasks)
        {
            for (var run = 1; run <= opts.Runs; run++)
            {
                Console.Write($"▶ {task.Id} #{run} ... ");
                var result = await RunOneAsync(task, run, ai, opts, outDir);
                results.Add(result);
                Console.WriteLine($"{(result.Functional ? "PASS" : "FAIL")} · точность: {(result.PrecisionNotes.Count == 0 ? "ок" : "замечания")} · шагов {result.Iterations} · токенов {result.Tokens.Total:N0} · {result.Seconds:0}с");
                foreach (var f in result.Failures) Console.WriteLine("    ✗ " + f);
                foreach (var p in result.PrecisionNotes) Console.WriteLine("    ~ " + p);
                if (result.Error is not null) Console.WriteLine("    ! " + result.Error);

                // Промежуточные результаты сохраняем после каждого прогона: ключ живёт недолго.
                await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"),
                    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        var report = BuildReport(results, ai, opts);
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), report);
        Console.WriteLine("\n" + report);
        return 0;
    }

    // ------------------------------------------------------------------ ad-hoc run on a real project

    // ADHOC: тот же раннер, устав и инструменты, что в продукте, но на копии настоящего проекта.
    // Нужно, чтобы мерить поведение на реальной задаче (сколько bash, правок, токенов, дошёл ли до сборки).
    //   dotnet run --project ConexyAI.Tests -- eval --dir <путь> --prompt "..." --timeout-min 30 --out <папка>
    private static async Task<int> RunAdHocAsync(EvalOptions opts)
    {
        var repoRoot = FindRepoRoot();
        var ai = LoadUpstream(repoRoot);
        if (string.IsNullOrWhiteSpace(ai.DeepSeek.ApiKey))
        {
            Console.Error.WriteLine("Ключ не задан: AiUpstream:DeepSeek:ApiKey в ConexyAI/appsettings.json или переменная AiUpstream__DeepSeek__ApiKey.");
            return 2;
        }

        var source = Path.GetFullPath(opts.Dir!);
        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"Папка не найдена: {source}");
            return 2;
        }

        var prompt = opts.Prompt;
        if (string.IsNullOrWhiteSpace(prompt) && opts.PromptFile is { Length: > 0 } promptFile && File.Exists(promptFile))
            prompt = await File.ReadAllTextAsync(promptFile);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("Нужен --prompt \"текст\" или --prompt-file путь.");
            return 2;
        }

        var outDir = opts.Out ?? Path.Combine(repoRoot, "eval-results", "adhoc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Path.Combine(outDir, "transcripts"));
        var workspaceRoot = Path.Combine(outDir, "workspace");
        var chatId = Guid.NewGuid();
        var dir = Path.Combine(workspaceRoot, chatId.ToString("N"));

        Console.WriteLine($"Проект: {source}");
        Console.WriteLine($"Копия для прогона: {dir}");
        CopyProject(source, dir);
        Console.WriteLine("Копия готова.\n");

        var hub = new RecordingHubContext();
        var workspace = new ConexyWorkspaceService(
            Options.Create(new WorkspaceOptions { RootPath = workspaceRoot }),
            NullLogger<ConexyWorkspaceService>.Instance);

        var realLlm = new ConexyLlmClient(new HttpClient(), NullLogger<ConexyLlmClient>.Instance, Options.Create(ai), hub);
        var llm = new EvalLlm(realLlm);
        var bash = new EvalHostBash(workspace, maxTimeoutSeconds: 600);
        var editor = new ConexyEditorService(
            workspace, new ConexyEditorStateService(), new WorkspacePathValidator(workspace), hub,
            NullLogger<ConexyEditorService>.Instance);
        var todo = new ConexyTodoService(hub, NullLogger<ConexyTodoService>.Instance);
        var approval = new EvalApproval();

        // AGENT_SKILLS: в продукте скиллы лежат в БД; на стенде берём их из папки проекта, чтобы
        // проверить, как агент следует присланному скиллу (read_skill).
        var skillsDir = Directory.Exists(Path.Combine(repoRoot, "ConexyAI", ".agents", "skills"))
            ? Path.Combine(repoRoot, "ConexyAI", ".agents", "skills")
            : Path.Combine(repoRoot, ".agents", "skills");
        IServiceScopeFactory? scopeFactory = null;
        if (Directory.Exists(skillsDir))
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddSingleton<ISkillService>(new FolderSkillService(skillsDir));
            // ORCHESTRA: на стенде нет фабрики настоящих помощников — ведущий получает их отчёт-заглушку
            // вместо исключения, и прогон продолжается.
            services.AddSingleton<IConexyAgentRunner>(new NoWorkerRunner());
            scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        var runner = new ConexyAgentRunner(
            workspace,
            new UnavailableVisionService(),
            llm,
            githubService: null!,
            editorService: editor,
            bashService: bash,
            todoService: todo,
            webSearchService: null!,
            hub,
            dangerousCommandClassifier: approval,
            commandApproval: approval,
            pendingActionService: null!,
            new FakeSubscriptionService(),
            new StaticConversationService(),
            NullLogger<ConexyAgentRunner>.Instance,
            documentService: null!,
            Options.Create(new AgentOptions
            {
                MaxIterations = 60,
                AuditTimeoutSeconds = 45,
                AuditEnabled = false,
                MaxSelfCorrectionAttempts = 5,
            }),
            webPageFetcher: null!,
            chatSearch: null!,
            scopeFactory: scopeFactory,
            // TEST_RUNNER: без него агент, которому велено прогнать тесты, получает «run_tests недоступен»
            // и тратит шаги на обход — стенд должен повторять продукт.
            testRunnerService: new ConexyTestRunnerService(workspace));

        var job = new ConexyJob(Guid.NewGuid(), chatId, Guid.NewGuid(), ConexyModelType.ConexyCoder, prompt);
        var context = new ConversationContext(job.TaskId, job.ChatId, job.UserId, runner.GetSystemPrompt(job.ModelType), prompt);

        var before = SnapshotSources(dir);
        var stopwatch = Stopwatch.StartNew();
        string? answer = null;
        string? error = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(opts.TaskTimeoutMinutes));
        try
        {
            answer = await runner.RunLoopAsync(job, context, cts.Token);
        }
        catch (OperationCanceledException)
        {
            error = $"таймаут {opts.TaskTimeoutMinutes} мин";
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        stopwatch.Stop();

        var after = SnapshotSources(dir);
        var changes = Diff(before, after);
        var totalTokens = llm.Cached + llm.Uncached + llm.Output + llm.TotalOnly;

        Console.WriteLine($"\n=== Результат за {stopwatch.Elapsed.TotalSeconds:0} с ===");
        Console.WriteLine($"шагов модели: {llm.StreamCalls} · вызовов аудита: {llm.SendCalls}");
        Console.WriteLine($"токены: вход из кэша {llm.Cached:N0} · вход {llm.Uncached:N0} · выход {llm.Output:N0} · всего {totalTokens:N0}");
        Console.WriteLine($"инструменты: {string.Join(", ", llm.ToolCalls.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"))}");
        Console.WriteLine($"bash-команд: {bash.Commands.Count} (отклонено стендом: {bash.Denied})");
        Console.WriteLine($"изменено файлов: {changes.Count}, изменённых строк: {changes.Sum(c => c.Lines)}");
        foreach (var c in changes) Console.WriteLine($"  {c.Kind} {c.Path} ({c.Lines} стр.)");
        if (error is not null) Console.WriteLine($"\nОШИБКА: {error}");
        Console.WriteLine($"\nФинальный ответ:\n{answer}");

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "transcripts", "adhoc.json"),
            JsonSerializer.Serialize(new
            {
                source,
                project = dir,
                prompt,
                finalAnswer = answer,
                error,
                seconds = stopwatch.Elapsed.TotalSeconds,
                steps = llm.StreamCalls,
                tokens = new { cached = llm.Cached, uncached = llm.Uncached, output = llm.Output, total = totalTokens },
                toolCalls = llm.ToolCalls,
                bashCommands = bash.Commands,
                changes,
                messages = llm.LastMessages,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"),
            $"# Ad-hoc прогон агента\n\n" +
            $"- Проект: `{source}`\n- Копия: `{dir}`\n- Время: {stopwatch.Elapsed.TotalSeconds:0} с\n" +
            $"- Шагов: {llm.StreamCalls} · токенов всего: {totalTokens:N0}\n" +
            $"- bash-команд: {bash.Commands.Count} · отказов стенда: {bash.Denied}\n" +
            $"- Изменено файлов: {changes.Count}, строк: {changes.Sum(c => c.Lines)}\n" +
            (error is null ? string.Empty : $"- Ошибка: {error}\n") +
            "\n## Инструменты\n\n" +
            string.Join("\n", llm.ToolCalls.OrderByDescending(kv => kv.Value).Select(kv => $"- {kv.Key}: {kv.Value}")) +
            "\n\n## Изменения\n\n" +
            string.Join("\n", changes.Select(c => $"- {c.Kind} `{c.Path}` ({c.Lines} стр.)")) +
            $"\n\n## Финальный ответ\n\n{answer}\n");

        Console.WriteLine($"\nОтчёт: {Path.Combine(outDir, "report.md")}");
        return 0;
    }

    // Копия проекта для прогона: артефакты сборки и .git не нужны — агент должен работать с исходниками.
    private static readonly string[] CopySkipDirs = { "bin", "obj", ".git", ".vs", "node_modules" };

    private static void CopyProject(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(sub);
            if (CopySkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            CopyProject(sub, Path.Combine(destination, name));
        }
    }

    // Тексты проекта для отчёта об изменениях: без bin/obj и без гигабайтных моделей.
    private static readonly HashSet<string> SnapshotSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", "models", "dist", ".next",
    };

    private static Dictionary<string, string> SnapshotSources(string dir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return map;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (rel.Split('/').Any(part => SnapshotSkipDirs.Contains(part))) continue;
            try
            {
                if (new FileInfo(file).Length > 512 * 1024) continue;
                map[rel] = File.ReadAllText(file);
            }
            catch (IOException)
            {
            }
        }
        return map;
    }

    private static async Task<EvalRun> RunOneAsync(EvalTask task, int runNo, AiUpstreamOptions ai, EvalOptions opts, string outDir)
    {
        var root = NewTempDir($"{task.Id}-{runNo}");
        var run = new EvalRun { TaskId = task.Id, Run = runNo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var hub = new RecordingHubContext();
            var workspace = new ConexyWorkspaceService(
                Options.Create(new WorkspaceOptions { RootPath = root }),
                NullLogger<ConexyWorkspaceService>.Instance);

            var realLlm = new ConexyLlmClient(new HttpClient(), NullLogger<ConexyLlmClient>.Instance, Options.Create(ai), hub);
            var llm = new EvalLlm(realLlm);
            var bash = new EvalHostBash(workspace);
            var editor = new ConexyEditorService(
                workspace, new ConexyEditorStateService(), new WorkspacePathValidator(workspace), hub,
                NullLogger<ConexyEditorService>.Instance);
            var todo = new ConexyTodoService(hub, NullLogger<ConexyTodoService>.Instance);
            var approval = new EvalApproval();

            var runner = new ConexyAgentRunner(
                workspace,
                new UnavailableVisionService(),
                llm,
                githubService: null!,
                editorService: editor,
                bashService: bash,
                todoService: todo,
                webSearchService: null!,
                hub,
                dangerousCommandClassifier: approval,
                commandApproval: approval,
                pendingActionService: null!,
                new FakeSubscriptionService(),
                new StaticConversationService(),
                NullLogger<ConexyAgentRunner>.Instance,
                documentService: null!,
                Options.Create(new AgentOptions
                {
                    MaxIterations = 60,
                    AuditTimeoutSeconds = 45,
                    AuditEnabled = !opts.NoAudit,
                    MaxSelfCorrectionAttempts = 5,
                }),
                webPageFetcher: null!,
                chatSearch: null!);

            var job = new ConexyJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ConexyModelType.ConexyCoder, task.Prompt);
            var context = new ConversationContext(job.TaskId, job.ChatId, job.UserId, runner.GetSystemPrompt(job.ModelType), job.Prompt);

            var dir = workspace.GetTaskWorkspacePath(job.ChatId);
            var before = Materialize(task, dir);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(opts.TaskTimeoutMinutes));
            try
            {
                run.FinalAnswer = await runner.RunLoopAsync(job, context, cts.Token);
            }
            catch (OperationCanceledException)
            {
                run.Error = $"таймаут {opts.TaskTimeoutMinutes} мин";
            }
            catch (Exception ex)
            {
                run.Error = ex.GetType().Name + ": " + ex.Message;
            }

            stopwatch.Stop();
            run.Seconds = stopwatch.Elapsed.TotalSeconds;

            // Сначала снимок изменений агента, и только потом проверка: она сама пишет временные файлы.
            var after = Snapshot(dir);
            var changes = Diff(before, after);
            run.Changes = changes;
            run.Failures = await task.Verify(new EvalCtx(dir, before));
            run.Functional = run.Failures.Count == 0 && run.Error is null;
            run.PrecisionNotes = Precision(task, changes);

            run.Iterations = llm.StreamCalls;
            run.AuditCalls = llm.SendCalls;
            run.Tokens = new TokenStats(llm.Cached, llm.Uncached, llm.Output, llm.TotalOnly);
            run.ToolCalls = llm.ToolCalls.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            run.BashCommands = bash.Commands.Count;
            run.DeniedCommands = bash.Denied;
            var toolMessages = llm.LastMessages.Where(m => m.Role == "tool").Select(m => m.Text ?? string.Empty).ToList();
            run.ToolMessages = toolMessages.Count;
            run.ToolErrorHints = toolMessages.Count(t => ErrorHint.IsMatch(t));

            await File.WriteAllTextAsync(
                Path.Combine(outDir, "transcripts", $"{task.Id}-{runNo}.json"),
                JsonSerializer.Serialize(new
                {
                    task = task.Id,
                    prompt = task.Prompt,
                    finalAnswer = run.FinalAnswer,
                    bashCommands = bash.Commands,
                    messages = llm.LastMessages,
                }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception ex)
        {
            run.Error ??= ex.GetType().Name + ": " + ex.Message;
            run.Seconds = stopwatch.Elapsed.TotalSeconds;
        }
        finally
        {
            TryDelete(root);
        }
        return run;
    }

    private static readonly Regex ErrorHint = new(
        @"no_match|ambiguous|not found|DENIED|USER_REJECTED|Traceback|SyntaxError|Error:|error CS|FAIL|failed",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ------------------------------------------------------------------ report

    private static string BuildReport(List<EvalRun> results, AiUpstreamOptions ai, EvalOptions opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Agent eval");
        sb.AppendLine();
        sb.AppendLine($"Модель: `{ai.DeepSeek.FlashModel}` · аудит: {(opts.NoAudit ? "выкл" : "вкл")} · прогонов на задачу: {opts.Runs}");
        sb.AppendLine();
        sb.AppendLine("| Задача | Прошла | Точность | Шагов | Токены (вх.кэш / вх. / вых.) | Время | bash (отказов) | Ошибок инструментов* |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var group in results.GroupBy(r => r.TaskId))
        {
            var runs = group.ToList();
            var passed = runs.Count(r => r.Functional);
            var precise = runs.Count(r => r.PrecisionNotes.Count == 0);
            sb.AppendLine(
                $"| `{group.Key}` | {passed}/{runs.Count} | {precise}/{runs.Count} | {runs.Average(r => r.Iterations):0.#} | " +
                $"{runs.Average(r => r.Tokens.Cached):N0} / {runs.Average(r => r.Tokens.Uncached):N0} / {runs.Average(r => r.Tokens.Output):N0} | " +
                $"{runs.Average(r => r.Seconds):0}с | {runs.Average(r => r.BashCommands):0.#} ({runs.Sum(r => r.DeniedCommands)}) | " +
                $"{runs.Sum(r => r.ToolErrorHints)}/{runs.Sum(r => r.ToolMessages)} |");
        }

        var total = results.Count;
        sb.AppendLine();
        sb.AppendLine($"**Итого:** функционально {results.Count(r => r.Functional)}/{total}, " +
                      $"точные правки {results.Count(r => r.PrecisionNotes.Count == 0)}/{total}, " +
                      $"всего токенов {results.Sum(r => r.Tokens.Total):N0}.");
        sb.AppendLine();
        sb.AppendLine("\\* эвристика: сообщения инструментов в последнем запросе, где встречаются слова об ошибке (no_match, ambiguous, Error, FAIL…); это не точный счётчик.");

        foreach (var r in results.Where(r => !r.Functional || r.PrecisionNotes.Count > 0 || r.Error is not null))
        {
            sb.AppendLine();
            sb.AppendLine($"## `{r.TaskId}` #{r.Run}");
            foreach (var f in r.Failures) sb.AppendLine($"- ✗ {f}");
            foreach (var p in r.PrecisionNotes) sb.AppendLine($"- ~ {p}");
            if (r.Error is not null) sb.AppendLine($"- ! {r.Error}");
            sb.AppendLine($"- изменения: {(r.Changes.Count == 0 ? "нет" : string.Join(", ", r.Changes.Select(c => $"{c.Kind} {c.Path} ({c.Lines} стр.)")))}");
            sb.AppendLine($"- инструменты: {string.Join(", ", r.ToolCalls.Select(kv => $"{kv.Key}×{kv.Value}"))}");
            var answer = (r.FinalAnswer ?? string.Empty).Trim();
            if (answer.Length > 600) answer = answer[..600] + "…";
            if (answer.Length > 0) sb.AppendLine($"- финальный ответ: {answer.Replace("\n", " ")}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ fixtures / diff helpers

    private static Dictionary<string, string> Materialize(EvalTask task, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (rel, content) in task.Files)
        {
            var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        return Snapshot(dir);
    }

    private static readonly string[] SkippedDirs = { "node_modules", ".git" };

    internal static Dictionary<string, string> Snapshot(string dir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return map;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (rel.Split('/').Any(part => SkippedDirs.Contains(part))) continue;
            map[rel] = File.ReadAllText(file);
        }
        return map;
    }

    internal static List<FileChange> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var changes = new List<FileChange>();
        foreach (var (path, content) in after)
        {
            if (!before.TryGetValue(path, out var old))
                changes.Add(new FileChange("added", path, Lines(content).Length));
            else if (old != content)
                changes.Add(new FileChange("modified", path, ChangedLineCount(old, content)));
        }
        foreach (var (path, content) in before)
        {
            if (!after.ContainsKey(path))
                changes.Add(new FileChange("deleted", path, Lines(content).Length));
        }
        return changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    private static string[] Lines(string text) => text.Split('\n');

    /// <summary>Сколько строк добавлено + удалено (по LCS).</summary>
    internal static int ChangedLineCount(string a, string b)
    {
        var x = Lines(a);
        var y = Lines(b);
        if ((long)x.Length * y.Length > 4_000_000) return Math.Abs(x.Length - y.Length) + 1;
        var dp = new int[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
            for (var j = y.Length - 1; j >= 0; j--)
                dp[i, j] = x[i] == y[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        return x.Length + y.Length - 2 * dp[0, 0];
    }

    private static List<string> Precision(EvalTask task, List<FileChange> changes)
    {
        var notes = new List<string>();
        var collateral = changes.Where(c => !task.Allowed(c.Path, c.Kind)).ToList();
        if (collateral.Count > 0)
            notes.Add("лишние изменения: " + string.Join(", ", collateral.Select(c => $"{c.Kind} {c.Path}")));
        var lines = changes.Where(c => task.Allowed(c.Path, c.Kind)).Sum(c => c.Lines);
        if (task.MaxChangedLines is { } budget && lines > budget)
            notes.Add($"правка шире нужной: изменено {lines} строк при бюджете {budget}");
        return notes;
    }

    private static string NewTempDir(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "conexy-eval", $"{name}-{Guid.NewGuid():N}"[..Math.Min(60, name.Length + 33)]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ConexyAI.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private static AiUpstreamOptions LoadUpstream(string repoRoot)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(repoRoot, "ConexyAI", "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(repoRoot, "ConexyAI", "appsettings.Development.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        return config.GetSection(AiUpstreamOptions.SectionName).Get<AiUpstreamOptions>() ?? new AiUpstreamOptions();
    }
}

// ====================================================================== models

internal sealed record FileChange(string Kind, string Path, int Lines);

internal sealed record TokenStats(long Cached, long Uncached, long Output, long TotalOnly)
{
    public long Total => Cached + Uncached + Output + TotalOnly;
}

internal sealed class EvalRun
{
    public string TaskId { get; set; } = string.Empty;
    public int Run { get; set; }
    public bool Functional { get; set; }
    public List<string> Failures { get; set; } = new();
    public List<string> PrecisionNotes { get; set; } = new();
    public List<FileChange> Changes { get; set; } = new();
    public int Iterations { get; set; }
    public int AuditCalls { get; set; }
    public TokenStats Tokens { get; set; } = new(0, 0, 0, 0);
    public Dictionary<string, int> ToolCalls { get; set; } = new();
    public int BashCommands { get; set; }
    public int DeniedCommands { get; set; }
    public int ToolMessages { get; set; }
    public int ToolErrorHints { get; set; }
    public double Seconds { get; set; }
    public string? FinalAnswer { get; set; }
    public string? Error { get; set; }
}

internal sealed class EvalOptions
{
    public bool SelfCheck { get; init; }
    public bool List { get; init; }
    public bool NoAudit { get; init; }
    public int Runs { get; init; } = 1;
    public int TaskTimeoutMinutes { get; init; } = 10;
    public string? Out { get; init; }
    /// <summary>ADHOC: гонять настоящий проект из этой папки (копия делается рядом с отчётом).</summary>
    public string? Dir { get; init; }
    public string? Prompt { get; init; }
    public string? PromptFile { get; init; }
    public HashSet<string> Only { get; init; } = new(StringComparer.Ordinal);

    public static EvalOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new EvalOptions
        {
            SelfCheck = args.Contains("--selfcheck"),
            List = args.Contains("--list"),
            NoAudit = args.Contains("--no-audit"),
            Runs = int.TryParse(Value("--runs"), out var runs) && runs > 0 ? runs : 1,
            TaskTimeoutMinutes = int.TryParse(Value("--timeout-min"), out var t) && t > 0 ? t : 10,
            Out = Value("--out"),
            Dir = Value("--dir"),
            Prompt = Value("--prompt"),
            PromptFile = Value("--prompt-file"),
            Only = (Value("--only") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
        };
    }
}

internal sealed class EvalCtx
{
    public EvalCtx(string dir, Dictionary<string, string> original)
    {
        Dir = dir;
        Original = original;
    }

    public string Dir { get; }

    /// <summary>Содержимое файлов сразу после создания фикстуры (до агента).</summary>
    public Dictionary<string, string> Original { get; }

    public string Path(string rel) => System.IO.Path.Combine(Dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public bool Exists(string rel) => File.Exists(Path(rel));
    public string Read(string rel) => File.ReadAllText(Path(rel));
    public void Write(string rel, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path(rel))!);
        File.WriteAllText(Path(rel), content, new UTF8Encoding(false));
    }

    public Task<(int Exit, string Output)> Node(params string[] args) => EvalProc.RunAsync("node", args, Dir, TimeSpan.FromSeconds(60));

    public bool Unchanged(string rel) => Original.TryGetValue(rel, out var o) && Exists(rel) && Read(rel) == o;
}

internal sealed record EvalTask(
    string Id,
    string Title,
    string Prompt,
    Dictionary<string, string> Files,
    Func<EvalCtx, Task<List<string>>> Verify,
    Func<string, string, bool> Allowed,
    int? MaxChangedLines,
    Action<string> Solve);

// ====================================================================== process helper

internal static class EvalProc
{
    public static async Task<(int Exit, string Output)> RunAsync(
        string file, IEnumerable<string> args, string cwd, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ScrubEnvironment(psi);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new object();
        void Append(string? line)
        {
            if (line is null) return;
            lock (gate) output.AppendLine(line);
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, $"не удалось запустить {file}: {ex.Message}");
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            // Дочитываем хвост потоков.
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            lock (gate) output.AppendLine($"[таймаут {timeout.TotalSeconds:0} с, процесс убит]");
            return (-2, output.ToString());
        }
        lock (gate) return (process.ExitCode, output.ToString());
    }

    /// <summary>Дочерним процессам не отдаём ключи и токены из окружения.</summary>
    public static void ScrubEnvironment(ProcessStartInfo psi)
    {
        var secret = new Regex("KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL", RegexOptions.IgnoreCase);
        foreach (var name in psi.Environment.Keys.Where(k => secret.IsMatch(k)).ToList())
            psi.Environment.Remove(name);
    }
}

// ====================================================================== agent-side fakes

/// <summary>Считает шаги, инструменты и токены настоящего клиента и хранит последнюю переписку.</summary>
internal sealed class EvalLlm : IConexyLlmClient
{
    private readonly IConexyLlmClient _inner;

    public EvalLlm(IConexyLlmClient inner) => _inner = inner;

    public int StreamCalls { get; private set; }
    public int SendCalls { get; private set; }
    public long Cached { get; private set; }
    public long Uncached { get; private set; }
    public long Output { get; private set; }
    public long TotalOnly { get; private set; }
    public Dictionary<string, int> ToolCalls { get; } = new();
    public List<ChatMessage> LastMessages { get; private set; } = new();

    public async Task<LlmChatResult> SendChatAsync(
        ConexyModelType modelType, List<ChatMessage> messages, List<object> tools,
        string? reasoningEffort = null, Guid? taskId = null, CancellationToken ct = default)
    {
        SendCalls++;
        var result = await _inner.SendChatAsync(modelType, messages, tools, reasoningEffort, taskId, ct);
        Account(result.Usage, result.TotalTokens);
        return result;
    }

    public async IAsyncEnumerable<StreamDelta> StreamChatAsync(
        List<ChatMessage> messages, ConexyModelType modelType, string? reasoningEffort = null,
        List<object>? tools = null, string? toolChoice = null, Guid? taskId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamCalls++;
        LastMessages = messages.ToList();
        await foreach (var delta in _inner.StreamChatAsync(messages, modelType, reasoningEffort, tools, toolChoice, taskId, ct))
        {
            if (delta.ToolCalls is { } calls)
            {
                foreach (var call in calls)
                    ToolCalls[call.Function.Name] = ToolCalls.GetValueOrDefault(call.Function.Name) + 1;
            }
            Account(delta.Usage, delta.TotalTokens ?? 0);
            yield return delta;
        }
    }

    private void Account(LlmTokenUsage? usage, int total)
    {
        if (usage is { IsEmpty: false })
        {
            Cached += usage.CachedInput;
            Uncached += usage.UncachedInput;
            Output += usage.Output;
        }
        else if (total > 0)
        {
            TotalOnly += total;
        }
    }
}

internal sealed class EvalApproval : ICommandApprovalClassifier, IDangerousCommandClassifier
{
    // Подтверждение у человека здесь не нужно: безопасность обеспечивает EvalHostBash.
    public bool RequiresApproval(string command) => false;
    public bool IsDangerous(string command) => false;
}

/// <summary>
/// bash агента на хосте: рабочая папка — временный воркспейс, ключи вычищены из окружения, а команды,
/// которые лезут наружу (абсолютные пути, `..`, сеть, установка пакетов, git push…), отклоняются.
/// Это защита от случайностей: node/скрипт, написанный самим агентом, всё равно может читать диск.
/// </summary>
internal sealed class EvalHostBash : IConexyBashService
{
    private readonly IConexyWorkspaceService _workspace;
    private readonly string _bash;
    // ADHOC: на настоящем проекте первая сборка/тесты не укладываются в 120 с — лимит задаёт вызывающий.
    private readonly int _maxTimeoutSeconds;

    public EvalHostBash(IConexyWorkspaceService workspace, int maxTimeoutSeconds = 120)
    {
        _workspace = workspace;
        _bash = ResolveBash();
        _maxTimeoutSeconds = Math.Max(5, maxTimeoutSeconds);
    }

    public List<string> Commands { get; } = new();
    public int Denied { get; private set; }

    private static string ResolveBash()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CONEXY_EVAL_BASH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\Git\bin\bash.exe",
                     @"C:\Program Files (x86)\Git\bin\bash.exe",
                     "/bin/bash",
                     "/usr/bin/bash",
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return "bash";
    }

    public async Task<BashToolResult> ExecuteAsync(Guid sessionId, BashToolRequest request, bool emitStartEvent = true, CancellationToken ct = default)
    {
        var dir = _workspace.GetTaskWorkspacePath(sessionId);
        var command = request.Command ?? string.Empty;
        Commands.Add(command);

        if (Deny(command, dir) is { } reason)
        {
            Denied++;
            return new BashToolResult
            {
                Success = false,
                ExitCode = 126,
                Output = $"DENIED: {reason}. Команда отклонена политикой тестового стенда: работай только внутри текущей папки, без сети и установки пакетов.",
            };
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? 60, 5, _maxTimeoutSeconds));
        var (exit, output) = await EvalProc.RunAsync(_bash, new[] { "-c", command }, dir, timeout, ct);
        var timedOut = exit == -2;
        var truncated = output.Length > 12_000;
        if (truncated)
            output = output[..6_000] + "\n[... вывод обрезан ...]\n" + output[^6_000..];

        return new BashToolResult
        {
            Success = exit == 0,
            ExitCode = exit,
            Output = output,
            WasTruncated = truncated,
            WasTimedOut = timedOut,
            ErrorType = timedOut ? "timeout" : exit == -1 ? "spawn_failed" : null,
        };
    }

    private static readonly (Regex Pattern, string Reason)[] Rules =
    {
        (new Regex(@"\.\.[\\/]|(^|\s)\.\.(\s|$)", RegexOptions.Compiled), "выход из рабочей папки через .."),
        (new Regex(@"(^|[\s;&|(])(sudo|su|shutdown|reboot|mkfs|dd|chmod\s+-R|chown)\b", RegexOptions.Compiled), "системная команда"),
        (new Regex(@"\b(curl|wget|iwr|Invoke-WebRequest|nc|ncat|ssh|scp|sftp|ftp|telnet)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "сеть"),
        (new Regex(@"\bgit\s+(push|clone|remote|config|fetch|pull|credential)\b", RegexOptions.Compiled), "git с доступом наружу"),
        (new Regex(@"\b(npm|pnpm|yarn)\s+(i|install|ci|add|publish|exec|update|upgrade)\b|\bnpx\b|\bpip3?\b|\bdotnet\s+(restore|add|tool)\b", RegexOptions.Compiled), "установка пакетов"),
        (new Regex(@"\b(cmd(\.exe)?|powershell|pwsh|reg|taskkill|wmic|schtasks|setx)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "оболочка/системная утилита Windows"),
        (new Regex(@"\b(printenv|env)\b|appsettings|\.env\b|\.ssh|id_rsa|\.aws|\.npmrc", RegexOptions.IgnoreCase | RegexOptions.Compiled), "доступ к окружению и секретам"),
        (new Regex(@"(?<=^|[\s""'=(:;|&<>])~|\$HOME|\$\{HOME\}|%USERPROFILE%|\$USERPROFILE", RegexOptions.Compiled), "домашняя папка"),
    };

    private static readonly Regex AbsoluteUnix = new(@"(?<=^|[\s""'=(:;|&<>])/(?!/)[\w.\-]", RegexOptions.Compiled);
    private static readonly Regex AbsoluteWindows = new(@"(?<![\w])[A-Za-z]:[\\/]", RegexOptions.Compiled);

    internal static string? Deny(string command, string workspaceDir)
    {
        foreach (var (pattern, reason) in Rules)
        {
            if (pattern.IsMatch(command)) return reason;
        }

        // Пути самого воркспейса (агент видит его через pwd) разрешены — вычёркиваем их перед проверкой.
        var cleaned = command.Replace("/dev/null", string.Empty, StringComparison.Ordinal);
        foreach (var form in WorkspaceForms(workspaceDir))
            cleaned = cleaned.Replace(form, string.Empty, StringComparison.OrdinalIgnoreCase);

        if (AbsoluteWindows.IsMatch(cleaned) || AbsoluteUnix.IsMatch(cleaned))
            return "абсолютный путь вне рабочей папки";
        return null;
    }

    private static IEnumerable<string> WorkspaceForms(string dir)
    {
        yield return dir;
        yield return dir.Replace('\\', '/');
        if (dir.Length > 2 && dir[1] == ':')
            yield return "/" + char.ToLowerInvariant(dir[0]) + dir[2..].Replace('\\', '/');
    }
}

// ====================================================================== tasks

internal static class EvalTasks
{
    // Сырые строковые литералы наследуют перевод строки файла исходников (на Windows это CRLF) — приводим к LF.
    private static string F(string text) => text.Replace("\r\n", "\n") + "\n";
    private static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
    private static string Tabs(string text) =>
        Regex.Replace(text, @"(?m)^( {4})+", m => new string('\t', m.Length / 4));

    private const string PackageJson = """{"name":"fixture","version":"1.0.0","scripts":{"test":"node --test"}}""";

    private static bool OnlyFiles(string path, params string[] allowed) => allowed.Contains(path);

    private static string ReadIn(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
    private static void WriteIn(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static async Task AddTestRun(EvalCtx ctx, List<string> failures, string label = "node --test")
    {
        var (exit, output) = await ctx.Node("--test");
        if (exit != 0)
            failures.Add($"{label} упал (код {exit}): {Tail(output, 600)}");
    }

    private static string Tail(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : "…" + text[^max..];
    }

    public static IReadOnlyList<EvalTask> All { get; } = Build();

    private static List<EvalTask> Build() => new()
    {
        FixMeanBug(),
        SurgicalTaxRate(),
        AddFeatureMultiFile(),
        BuildInventoryProject(),
        RenameAcrossFiles(),
        BugFromSymptom(),
        PreserveCrlfAndTabs(),
        FollowProjectRules(),
    };

    // ---------------------------------------------------------------- 1. простая правка по падающему тесту

    private static EvalTask FixMeanBug() => new(
        Id: "fix-mean-bug",
        Title: "Найти и починить баг по падающему тесту (правка в 1 строке)",
        Prompt: "Тест в test/stats.test.js падает. Найди причину и исправь баг в коде. Сами тесты не меняй.",
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["src/stats.js"] = F("""
                'use strict';

                function sum(values) {
                  let total = 0;
                  for (const v of values) total += v;
                  return total;
                }

                function mean(values) {
                  if (values.length === 0) return 0;
                  return sum(values) / (values.length - 1);
                }

                function median(values) {
                  if (values.length === 0) return 0;
                  const sorted = [...values].sort((a, b) => a - b);
                  const mid = Math.floor(sorted.length / 2);
                  return sorted.length % 2 === 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
                }

                function max(values) {
                  return values.reduce((a, b) => (b > a ? b : a), -Infinity);
                }

                module.exports = { sum, mean, median, max };
                """),
            ["test/stats.test.js"] = F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const { sum, mean, median, max } = require('../src/stats');

                test('sum adds values', () => assert.strictEqual(sum([1, 2, 3]), 6));
                test('mean of 2,4,6 is 4', () => assert.strictEqual(mean([2, 4, 6]), 4));
                test('mean of one value', () => assert.strictEqual(mean([5]), 5));
                test('mean of empty is 0', () => assert.strictEqual(mean([]), 0));
                test('median odd', () => assert.strictEqual(median([3, 1, 2]), 2));
                test('median even', () => assert.strictEqual(median([4, 1, 3, 2]), 2.5));
                test('max', () => assert.strictEqual(max([1, 9, 3]), 9));
                """),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            await AddTestRun(ctx, failures);
            if (!ctx.Unchanged("test/stats.test.js")) failures.Add("тесты изменены");
            return failures;
        },
        Allowed: (p, _) => OnlyFiles(p, "src/stats.js"),
        MaxChangedLines: 2,
        Solve: dir => WriteIn(dir, "src/stats.js", ReadIn(dir, "src/stats.js").Replace("(values.length - 1)", "values.length")));

    // ---------------------------------------------------------------- 2. хирургическая правка среди похожих кусков

    private static EvalTask SurgicalTaxRate() => new(
        Id: "surgical-tax-rate",
        Title: "Точечная правка среди похожих функций (одна страна из шести)",
        Prompt: "Ставка НДС в Германии выросла с 19% до 21%. Обнови её везде в src/pricing.js (функция, комментарий и таблица ставок). Остальные страны не трогай.",
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["src/pricing.js"] = F("""
                'use strict';

                // Tax calculators per country. Rates are applied to the net price.

                function round2(n) {
                  return Math.round(n * 100) / 100;
                }

                // FR: 20%
                function priceWithTaxFR(net) {
                  const rate = 1.2;
                  return round2(net * rate);
                }

                // DE: 19%
                function priceWithTaxDE(net) {
                  const rate = 1.19;
                  return round2(net * rate);
                }

                // ES: 21%
                function priceWithTaxES(net) {
                  const rate = 1.21;
                  return round2(net * rate);
                }

                // IT: 22%
                function priceWithTaxIT(net) {
                  const rate = 1.22;
                  return round2(net * rate);
                }

                // PL: 23%
                function priceWithTaxPL(net) {
                  const rate = 1.23;
                  return round2(net * rate);
                }

                // NL: 21%
                function priceWithTaxNL(net) {
                  const rate = 1.21;
                  return round2(net * rate);
                }

                const RATES = {
                  FR: 20,
                  DE: 19,
                  ES: 21,
                  IT: 22,
                  PL: 23,
                  NL: 21,
                };

                function describeRate(code) {
                  return `${code}: ${RATES[code]}%`;
                }

                module.exports = {
                  priceWithTaxFR,
                  priceWithTaxDE,
                  priceWithTaxES,
                  priceWithTaxIT,
                  priceWithTaxPL,
                  priceWithTaxNL,
                  RATES,
                  describeRate,
                };
                """),
            ["test/pricing.test.js"] = F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const p = require('../src/pricing');

                test('DE is 21%', () => assert.strictEqual(p.priceWithTaxDE(100), 121));
                test('DE rate table', () => assert.strictEqual(p.RATES.DE, 21));
                test('DE description', () => assert.strictEqual(p.describeRate('DE'), 'DE: 21%'));
                test('FR unchanged', () => assert.strictEqual(p.priceWithTaxFR(100), 120));
                test('ES unchanged', () => assert.strictEqual(p.priceWithTaxES(100), 121));
                test('IT unchanged', () => assert.strictEqual(p.priceWithTaxIT(100), 122));
                test('PL unchanged', () => assert.strictEqual(p.priceWithTaxPL(100), 123));
                test('NL unchanged', () => assert.strictEqual(p.priceWithTaxNL(100), 121));
                test('other rates unchanged', () => {
                  assert.deepStrictEqual({ ...p.RATES, DE: 0 }, { FR: 20, DE: 0, ES: 21, IT: 22, PL: 23, NL: 21 });
                });
                """),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            await AddTestRun(ctx, failures);
            if (!ctx.Unchanged("test/pricing.test.js")) failures.Add("тесты изменены");

            // Всё, кроме блока DE и строки DE в таблице, должно совпадать с оригиналом байт в байт.
            static string Strip(string text) =>
                Regex.Replace(
                    Regex.Replace(text, @"// DE[^\n]*\nfunction priceWithTaxDE[\s\S]*?\n}\n\n", string.Empty),
                    @"(?m)^\s*DE:.*\r?\n", string.Empty);
            if (ctx.Exists("src/pricing.js") && Strip(ctx.Read("src/pricing.js")) != Strip(ctx.Original["src/pricing.js"]))
                failures.Add("затронут код других стран или структура файла");
            if (ctx.Exists("src/pricing.js") && Regex.IsMatch(ctx.Read("src/pricing.js"), @"19%|1\.19|DE:\s*19"))
                failures.Add("в файле остались следы старой ставки 19%");
            return failures;
        },
        Allowed: (p, _) => OnlyFiles(p, "src/pricing.js"),
        MaxChangedLines: 6,
        Solve: dir =>
        {
            var text = ReadIn(dir, "src/pricing.js")
                .Replace("// DE: 19%", "// DE: 21%")
                .Replace("const rate = 1.19;", "const rate = 1.21;")
                .Replace("DE: 19,", "DE: 21,");
            WriteIn(dir, "src/pricing.js", text);
        });

    // ---------------------------------------------------------------- 3. новая функция + экспорт + документация + тесты

    private static EvalTask AddFeatureMultiFile() => new(
        Id: "add-feature-multifile",
        Title: "Новая функция: код, экспорт, README и собственные тесты",
        Prompt: """
            Добавь функцию slugify(text) в src/text.js:
            - приводит к нижнему регистру;
            - пробелы и подчёркивания превращает в '-';
            - удаляет всё, кроме a-z, 0-9 и '-' (кириллица просто удаляется, транслитерация не нужна);
            - схлопывает повторяющиеся '-' и обрезает '-' по краям.
            Экспортируй её из src/index.js, добавь описание в раздел API файла README.md и напиши тесты в новом файле test/slugify.test.js (минимум 4 кейса). Существующие тесты должны остаться зелёными.
            """,
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["src/text.js"] = F("""
                'use strict';

                function capitalize(text) {
                  if (!text) return '';
                  return text[0].toUpperCase() + text.slice(1);
                }

                function truncate(text, max) {
                  if (text.length <= max) return text;
                  return text.slice(0, Math.max(0, max - 1)) + '…';
                }

                module.exports = { capitalize, truncate };
                """),
            ["src/index.js"] = F("""
                const { capitalize, truncate } = require('./text');

                module.exports = { capitalize, truncate };
                """),
            ["README.md"] = F("""
                # textkit

                Small text helpers.

                ## API

                - `capitalize(text)` — uppercases the first letter.
                - `truncate(text, max)` — cuts text to `max` characters, adding an ellipsis.
                """),
            ["test/text.test.js"] = F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const { capitalize, truncate } = require('../src');

                test('capitalize', () => assert.strictEqual(capitalize('hello'), 'Hello'));
                test('truncate', () => assert.strictEqual(truncate('abcdef', 4), 'abc…'));
                """),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            await AddTestRun(ctx, failures);

            if (!ctx.Exists("test/slugify.test.js")) failures.Add("нет test/slugify.test.js");
            else if (Regex.Matches(ctx.Read("test/slugify.test.js"), @"\btest\(").Count < 4) failures.Add("в test/slugify.test.js меньше 4 тестов");
            if (!ctx.Exists("README.md") || !ctx.Read("README.md").Contains("slugify")) failures.Add("README.md не описывает slugify");
            if (!ctx.Original["src/text.js"].Contains("truncate") || !ctx.Read("src/text.js").Contains("truncate")) failures.Add("потеряна существующая функция truncate");

            ctx.Write("__hidden.test.js", F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const idx = require('./src/index');
                test('slugify exported from index', () => assert.strictEqual(typeof idx.slugify, 'function'));
                test('mixed', () => assert.strictEqual(idx.slugify('  Hello,  World_foo '), 'hello-world-foo'));
                test('collapse and trim', () => assert.strictEqual(idx.slugify('--A--b--'), 'a-b'));
                test('cyrillic removed', () => assert.strictEqual(idx.slugify('Привет'), ''));
                test('digits kept', () => assert.strictEqual(idx.slugify('Version 2.0 beta'), 'version-20-beta'));
                test('empty', () => assert.strictEqual(idx.slugify(''), ''));
                test('old exports intact', () => assert.strictEqual(idx.capitalize('x'), 'X'));
                """));
            var (exit, output) = await ctx.Node("--test", "__hidden.test.js");
            File.Delete(ctx.Path("__hidden.test.js"));
            if (exit != 0) failures.Add("скрытые проверки slugify не прошли: " + Tail(output, 600));
            return failures;
        },
        Allowed: (p, _) => OnlyFiles(p, "src/text.js", "src/index.js", "README.md", "test/slugify.test.js"),
        MaxChangedLines: 60,
        Solve: dir =>
        {
            WriteIn(dir, "src/text.js", ReadIn(dir, "src/text.js").Replace("module.exports = { capitalize, truncate };", F("""
                function slugify(text) {
                  return text
                    .toLowerCase()
                    .replace(/[\s_]+/g, '-')
                    .replace(/[^a-z0-9-]/g, '')
                    .replace(/-+/g, '-')
                    .replace(/^-|-$/g, '');
                }

                module.exports = { capitalize, truncate, slugify };
                """).TrimEnd('\n')));
            WriteIn(dir, "src/index.js", F("""
                const { capitalize, truncate, slugify } = require('./text');

                module.exports = { capitalize, truncate, slugify };
                """));
            WriteIn(dir, "README.md", ReadIn(dir, "README.md") + "- `slugify(text)` — makes a URL-safe slug.\n");
            WriteIn(dir, "test/slugify.test.js", F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const { slugify } = require('../src');

                test('lowercases', () => assert.strictEqual(slugify('ABC'), 'abc'));
                test('spaces', () => assert.strictEqual(slugify('a b_c'), 'a-b-c'));
                test('strips', () => assert.strictEqual(slugify('a!b'), 'ab'));
                test('trims', () => assert.strictEqual(slugify('--a--'), 'a'));
                """));
        });

    // ---------------------------------------------------------------- 4. проект «с нуля» по спецификации

    private static EvalTask BuildInventoryProject() => new(
        Id: "build-inventory-cli",
        Title: "Многошаговый проект по SPEC.md: модуль, хранилище, CLI, точка входа",
        Prompt: "Прочитай SPEC.md и реализуй проект так, чтобы все тесты в test/ проходили и CLI работал как описано. Тесты не меняй. Запусти тесты и убедись, что они зелёные, и один раз проверь bin/inv.js руками.",
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["SPEC.md"] = F("""
                # Inventory

                CommonJS, без внешних зависимостей. Структура: `src/inventory.js`, `src/storage.js`, `src/cli.js`, `bin/inv.js`.

                ## src/inventory.js
                Экспортирует `{ Inventory }`. Класс `Inventory`:
                - `add(sku, qty, price)` — добавляет qty штук. Если sku уже есть, qty суммируется, а price заменяется на новый.
                  qty — целое > 0, иначе `Error('INVALID_QTY')`. price — число >= 0, иначе `Error('INVALID_PRICE')`.
                - `remove(sku, qty)` — списывает. qty проверяется как в add (`INVALID_QTY`). Нет такого sku — `Error('UNKNOWN_SKU')`.
                  qty больше остатка — `Error('INSUFFICIENT_STOCK')`. Когда остаток стал 0, sku удаляется.
                - `totalValue()` — сумма qty*price по всем позициям, округлена до 2 знаков (число).
                - `lowStock(threshold)` — массив sku, у которых qty < threshold, отсортирован по алфавиту.
                - `toJSON()` — `{ items: { [sku]: { qty, price } } }`.
                - `static fromJSON(obj)` — создаёт Inventory из результата toJSON.

                ## src/storage.js
                Экспортирует `{ save, load }`.
                - `save(file, inventory)` — пишет `inventory.toJSON()` в файл как JSON с отступом 2 пробела.
                - `load(file)` — читает файл в Inventory; если файла нет — пустой Inventory.

                ## src/cli.js
                Экспортирует `{ run }`. `run(argv, io)` возвращает код выхода (число); `io = { out(line), err(line) }`.
                `argv` — без node и имени скрипта: `['--file', path, command, ...args]`.
                Команды (аргументы qty и price разбираются через `Number`):
                - `add <sku> <qty> <price>` → out `ok`, сохраняет файл
                - `remove <sku> <qty>` → out `ok`, сохраняет файл
                - `list` → по строке на позицию, по алфавиту: `<sku> x<qty> @ <price с 2 знаками>`; если пусто — `(empty)`
                - `value` → `total: <сумма с 2 знаками>`
                - `low <threshold>` → sku через запятую или `(none)`
                Ошибка из Inventory → err `error: <message>`, код 1. Неизвестная команда → err `error: unknown command`, код 2.
                Нет `--file` → err `error: missing --file`, код 2. Успех — код 0.

                ## bin/inv.js
                Тонкая обёртка: `process.exit(run(process.argv.slice(2), { out: console.log, err: console.error }))`.
                """),
            ["test/inventory.test.js"] = F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const { Inventory } = require('../src/inventory');

                test('add sums quantities and replaces price', () => {
                  const inv = new Inventory();
                  inv.add('A', 2, 1.5);
                  inv.add('A', 3, 2);
                  assert.deepStrictEqual(inv.toJSON(), { items: { A: { qty: 5, price: 2 } } });
                });

                test('add validates qty and price', () => {
                  const inv = new Inventory();
                  assert.throws(() => inv.add('A', 0, 1), /INVALID_QTY/);
                  assert.throws(() => inv.add('A', 1.5, 1), /INVALID_QTY/);
                  assert.throws(() => inv.add('A', 1, -1), /INVALID_PRICE/);
                });

                test('remove reduces stock and deletes at zero', () => {
                  const inv = new Inventory();
                  inv.add('A', 5, 1);
                  inv.remove('A', 2);
                  assert.strictEqual(inv.toJSON().items.A.qty, 3);
                  inv.remove('A', 3);
                  assert.deepStrictEqual(inv.toJSON(), { items: {} });
                });

                test('remove errors', () => {
                  const inv = new Inventory();
                  inv.add('A', 1, 1);
                  assert.throws(() => inv.remove('B', 1), /UNKNOWN_SKU/);
                  assert.throws(() => inv.remove('A', 2), /INSUFFICIENT_STOCK/);
                  assert.throws(() => inv.remove('A', 0), /INVALID_QTY/);
                });

                test('totalValue is rounded to 2 decimals', () => {
                  const inv = new Inventory();
                  inv.add('A', 3, 0.1);
                  assert.strictEqual(inv.totalValue(), 0.3);
                });

                test('lowStock is sorted and strict', () => {
                  const inv = new Inventory();
                  inv.add('b', 1, 1);
                  inv.add('a', 2, 1);
                  inv.add('c', 3, 1);
                  assert.deepStrictEqual(inv.lowStock(3), ['a', 'b']);
                });

                test('fromJSON round trip', () => {
                  const inv = new Inventory();
                  inv.add('A', 2, 3);
                  assert.deepStrictEqual(Inventory.fromJSON(inv.toJSON()).toJSON(), inv.toJSON());
                });
                """),
            ["test/storage.test.js"] = F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const fs = require('node:fs');
                const os = require('node:os');
                const path = require('node:path');
                const { Inventory } = require('../src/inventory');
                const { save, load } = require('../src/storage');

                function tmp() {
                  return path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'inv-')), 'store.json');
                }

                test('save and load round trip', () => {
                  const file = tmp();
                  const inv = new Inventory();
                  inv.add('A', 2, 1.25);
                  save(file, inv);
                  assert.deepStrictEqual(load(file).toJSON(), inv.toJSON());
                  assert.ok(fs.readFileSync(file, 'utf8').includes('\n  '), 'indented with 2 spaces');
                });

                test('load of a missing file is empty', () => {
                  assert.deepStrictEqual(load(tmp()).toJSON(), { items: {} });
                });
                """),
            ["test/cli.test.js"] = F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const fs = require('node:fs');
                const os = require('node:os');
                const path = require('node:path');
                const { run } = require('../src/cli');

                function setup() {
                  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'cli-')), 's.json');
                  return (...args) => {
                    const out = [];
                    const err = [];
                    const code = run(['--file', file, ...args], { out: (l) => out.push(l), err: (l) => err.push(l) });
                    return { code, out, err };
                  };
                }

                test('add, list, value', () => {
                  const cli = setup();
                  assert.deepStrictEqual(cli('add', 'B', '1', '10').out, ['ok']);
                  cli('add', 'A', '5', '2.5');
                  assert.deepStrictEqual(cli('list').out, ['A x5 @ 2.50', 'B x1 @ 10.00']);
                  assert.deepStrictEqual(cli('value').out, ['total: 22.50']);
                });

                test('empty list', () => {
                  assert.deepStrictEqual(setup()('list').out, ['(empty)']);
                });

                test('low stock', () => {
                  const cli = setup();
                  cli('add', 'A', '5', '1');
                  cli('add', 'B', '1', '1');
                  assert.deepStrictEqual(cli('low', '3').out, ['B']);
                  assert.deepStrictEqual(cli('low', '1').out, ['(none)']);
                });

                test('domain errors give code 1', () => {
                  const cli = setup();
                  const r = cli('remove', 'X', '1');
                  assert.strictEqual(r.code, 1);
                  assert.deepStrictEqual(r.err, ['error: UNKNOWN_SKU']);
                });

                test('unknown command gives code 2', () => {
                  const r = setup()('frobnicate');
                  assert.strictEqual(r.code, 2);
                  assert.deepStrictEqual(r.err, ['error: unknown command']);
                });

                test('missing --file gives code 2', () => {
                  const err = [];
                  const code = run(['list'], { out() {}, err: (l) => err.push(l) });
                  assert.strictEqual(code, 2);
                  assert.deepStrictEqual(err, ['error: missing --file']);
                });
                """),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            await AddTestRun(ctx, failures);
            foreach (var test in ctx.Original.Keys.Where(k => k.StartsWith("test/", StringComparison.Ordinal)))
                if (!ctx.Unchanged(test)) failures.Add($"изменён {test}");

            // Сквозная проверка настоящего бинарника — тесты его не запускают.
            if (!ctx.Exists("bin/inv.js")) { failures.Add("нет bin/inv.js"); return failures; }
            var store = Path.Combine(ctx.Dir, "e2e-store.json");
            async Task<(int Exit, string Output)> Inv(params string[] args) =>
                await ctx.Node(new[] { "bin/inv.js", "--file", store }.Concat(args).ToArray());

            await Inv("add", "A", "5", "2.5");
            await Inv("add", "B", "1", "10");
            var value = await Inv("value");
            if (value.Exit != 0 || !value.Output.Contains("total: 22.50")) failures.Add("bin/inv.js value: " + Tail(value.Output, 200));
            var low = await Inv("low", "3");
            if (low.Output.Trim() != "B") failures.Add("bin/inv.js low 3: " + Tail(low.Output, 200));
            var bad = await Inv("remove", "A", "9");
            if (bad.Exit != 1 || !bad.Output.Contains("error: INSUFFICIENT_STOCK")) failures.Add($"bin/inv.js remove: код {bad.Exit}, {Tail(bad.Output, 200)}");
            var unknown = await Inv("nope");
            if (unknown.Exit != 2) failures.Add($"bin/inv.js nope: код {unknown.Exit}");
            File.Delete(store);
            return failures;
        },
        Allowed: (p, kind) => kind == "added" && (p.StartsWith("src/", StringComparison.Ordinal) || p.StartsWith("bin/", StringComparison.Ordinal)),
        MaxChangedLines: null,
        Solve: dir =>
        {
            WriteIn(dir, "src/inventory.js", F("""
                'use strict';

                function checkQty(qty) {
                  if (!Number.isInteger(qty) || qty <= 0) throw new Error('INVALID_QTY');
                }

                class Inventory {
                  constructor() {
                    this.items = new Map();
                  }

                  add(sku, qty, price) {
                    checkQty(qty);
                    if (typeof price !== 'number' || Number.isNaN(price) || price < 0) throw new Error('INVALID_PRICE');
                    const cur = this.items.get(sku);
                    this.items.set(sku, { qty: (cur ? cur.qty : 0) + qty, price });
                  }

                  remove(sku, qty) {
                    checkQty(qty);
                    const cur = this.items.get(sku);
                    if (!cur) throw new Error('UNKNOWN_SKU');
                    if (qty > cur.qty) throw new Error('INSUFFICIENT_STOCK');
                    if (cur.qty === qty) this.items.delete(sku);
                    else cur.qty -= qty;
                  }

                  totalValue() {
                    let total = 0;
                    for (const { qty, price } of this.items.values()) total += qty * price;
                    return Math.round(total * 100) / 100;
                  }

                  lowStock(threshold) {
                    return [...this.items.entries()].filter(([, v]) => v.qty < threshold).map(([k]) => k).sort();
                  }

                  toJSON() {
                    const items = {};
                    for (const [sku, v] of this.items) items[sku] = { qty: v.qty, price: v.price };
                    return { items };
                  }

                  static fromJSON(obj) {
                    const inv = new Inventory();
                    for (const [sku, v] of Object.entries(obj.items || {})) inv.items.set(sku, { qty: v.qty, price: v.price });
                    return inv;
                  }
                }

                module.exports = { Inventory };
                """));
            WriteIn(dir, "src/storage.js", F("""
                'use strict';
                const fs = require('node:fs');
                const { Inventory } = require('./inventory');

                function save(file, inventory) {
                  fs.writeFileSync(file, JSON.stringify(inventory.toJSON(), null, 2));
                }

                function load(file) {
                  if (!fs.existsSync(file)) return new Inventory();
                  return Inventory.fromJSON(JSON.parse(fs.readFileSync(file, 'utf8')));
                }

                module.exports = { save, load };
                """));
            WriteIn(dir, "src/cli.js", F("""
                'use strict';
                const { load, save } = require('./storage');

                function run(argv, io) {
                  if (argv[0] !== '--file' || !argv[1]) {
                    io.err('error: missing --file');
                    return 2;
                  }
                  const file = argv[1];
                  const [command, ...args] = argv.slice(2);
                  const known = ['add', 'remove', 'list', 'value', 'low'];
                  if (!known.includes(command)) {
                    io.err('error: unknown command');
                    return 2;
                  }
                  try {
                    const inv = load(file);
                    switch (command) {
                      case 'add':
                        inv.add(args[0], Number(args[1]), Number(args[2]));
                        save(file, inv);
                        io.out('ok');
                        break;
                      case 'remove':
                        inv.remove(args[0], Number(args[1]));
                        save(file, inv);
                        io.out('ok');
                        break;
                      case 'list': {
                        const items = Object.entries(inv.toJSON().items).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
                        if (items.length === 0) io.out('(empty)');
                        for (const [sku, v] of items) io.out(`${sku} x${v.qty} @ ${v.price.toFixed(2)}`);
                        break;
                      }
                      case 'value':
                        io.out(`total: ${inv.totalValue().toFixed(2)}`);
                        break;
                      case 'low': {
                        const low = inv.lowStock(Number(args[0]));
                        io.out(low.length ? low.join(',') : '(none)');
                        break;
                      }
                    }
                    return 0;
                  } catch (e) {
                    io.err(`error: ${e.message}`);
                    return 1;
                  }
                }

                module.exports = { run };
                """));
            WriteIn(dir, "bin/inv.js", F("""
                #!/usr/bin/env node
                const { run } = require('../src/cli');

                process.exit(run(process.argv.slice(2), { out: console.log, err: console.error }));
                """));
        });

    // ---------------------------------------------------------------- 5. переименование по нескольким файлам

    private static EvalTask RenameAcrossFiles() => new(
        Id: "rename-across-files",
        Title: "Переименование функции по всему коду без лишних затрагиваний",
        Prompt: "Функция getUsr названа с опечаткой. Переименуй её в getUser везде в коде и тестах (определение, импорты, вызовы, комментарии). Файл docs/CHANGELOG.md не трогай — это история. Остальные идентификаторы (например usrCache) не переименовывай.",
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["src/users.js"] = F("""
                'use strict';

                const usrCache = new Map();

                // getUsr: returns a cached user record
                function getUsr(id) {
                  if (usrCache.has(id)) return usrCache.get(id);
                  const usr = { id, name: `user-${id}` };
                  usrCache.set(id, usr);
                  return usr;
                }

                module.exports = { getUsr };
                """),
            ["src/orders.js"] = F("""
                'use strict';
                const { getUsr } = require('./users');

                const usrCache = []; // unrelated local list of recently seen orders

                function ordersFor(id) {
                  const usr = getUsr(id);
                  usrCache.push(id);
                  return [{ owner: usr.name, total: 10 }];
                }

                module.exports = { ordersFor };
                """),
            ["src/report.js"] = F("""
                'use strict';
                const users = require('./users');

                function line(id) {
                  return `report for ${users.getUsr(id).name}`;
                }

                module.exports = { line };
                """),
            ["src/api/handlers.js"] = F("""
                'use strict';
                const { getUsr } = require('../users');

                // delegates to getUsr
                function handleGetUser(req) {
                  return { status: 200, body: getUsr(req.id) };
                }

                module.exports = { handleGetUser };
                """),
            ["test/users.test.js"] = F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const { getUsr } = require('../src/users');
                const { ordersFor } = require('../src/orders');
                const { line } = require('../src/report');
                const { handleGetUser } = require('../src/api/handlers');

                test('getUsr caches', () => assert.strictEqual(getUsr(1), getUsr(1)));
                test('orders', () => assert.strictEqual(ordersFor(2)[0].owner, 'user-2'));
                test('report', () => assert.strictEqual(line(3), 'report for user-3'));
                test('handler', () => assert.strictEqual(handleGetUser({ id: 4 }).body.name, 'user-4'));
                """),
            ["docs/CHANGELOG.md"] = F("""
                # Changelog

                ## 1.0.1
                - Added the `getUsr` lookup (typo, to be fixed later).
                """),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            await AddTestRun(ctx, failures);
            if (!ctx.Unchanged("docs/CHANGELOG.md")) failures.Add("изменён docs/CHANGELOG.md");

            foreach (var rel in ctx.Original.Keys.Where(k => k.StartsWith("src/", StringComparison.Ordinal) || k.StartsWith("test/", StringComparison.Ordinal)))
            {
                if (!ctx.Exists(rel)) { failures.Add($"удалён {rel}"); continue; }
                var text = ctx.Read(rel);
                if (Regex.IsMatch(text, @"\bgetUsr\b")) failures.Add($"остался getUsr в {rel}");
                var before = Regex.Matches(ctx.Original[rel], @"\busrCache\b").Count;
                var after = Regex.Matches(text, @"\busrCache\b").Count;
                if (before != after) failures.Add($"затронут usrCache в {rel} ({before} → {after})");
            }
            if (!ctx.Exists("src/users.js") || !Regex.IsMatch(ctx.Read("src/users.js"), @"module\.exports\s*=\s*\{\s*getUser\s*\}"))
                failures.Add("src/users.js не экспортирует getUser");
            return failures;
        },
        Allowed: (p, _) => p.StartsWith("src/", StringComparison.Ordinal) || p.StartsWith("test/", StringComparison.Ordinal),
        MaxChangedLines: 24,
        Solve: dir =>
        {
            foreach (var rel in new[] { "src/users.js", "src/orders.js", "src/report.js", "src/api/handlers.js", "test/users.test.js" })
                WriteIn(dir, rel, Regex.Replace(ReadIn(dir, rel), @"\bgetUsr\b", "getUser"));
        });

    // ---------------------------------------------------------------- 6. поиск багов по симптому

    private static EvalTask BugFromSymptom() => new(
        Id: "bug-from-symptom",
        Title: "Найти два бага по описанию симптомов (тестов нет)",
        Prompt: "Запусти `node report.js`: даты в отчёте неверные — ожидается, что срок «Design» — 2024-02-02, а «Build» — 2024-03-03 (старты 2024-01-30 и 2024-02-27 плюс 3 и 5 дней). Кроме того, после addDays исходный объект даты тоже меняется, а так быть не должно. Найди причины в коде и исправь их, не ломая публичный интерфейс src/dates.js.",
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["src/dates.js"] = F("""
                'use strict';

                function parseISODate(text) {
                  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
                  if (!m) throw new Error('INVALID_DATE');
                  const [, y, mo, d] = m;
                  return new Date(Number(y), Number(mo), Number(d));
                }

                function formatDate(date) {
                  const y = date.getFullYear();
                  const m = String(date.getMonth() + 1).padStart(2, '0');
                  const d = String(date.getDate()).padStart(2, '0');
                  return `${y}-${m}-${d}`;
                }

                function addDays(date, days) {
                  date.setDate(date.getDate() + days);
                  return date;
                }

                function daysBetween(a, b) {
                  return Math.round((b - a) / 86400000);
                }

                module.exports = { parseISODate, formatDate, addDays, daysBetween };
                """),
            ["report.js"] = F("""
                const { parseISODate, formatDate, addDays } = require('./src/dates');

                const tasks = [
                  { name: 'Design', start: '2024-01-30', days: 3 },
                  { name: 'Build', start: '2024-02-27', days: 5 },
                ];

                for (const t of tasks) {
                  const start = parseISODate(t.start);
                  const due = addDays(start, t.days);
                  console.log(`${t.name}: ${formatDate(parseISODate(t.start))} -> ${formatDate(due)}`);
                }
                """),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            var report = await ctx.Node("report.js");
            var expected = "Design: 2024-01-30 -> 2024-02-02\nBuild: 2024-02-27 -> 2024-03-03";
            if (report.Output.Replace("\r\n", "\n").Trim() != expected)
                failures.Add("вывод report.js неверный: " + Tail(report.Output, 300));

            ctx.Write("__hidden.test.js", F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const d = require('./src/dates');
                test('parse month', () => assert.strictEqual(d.parseISODate('2024-03-15').getMonth(), 2));
                test('round trip', () => assert.strictEqual(d.formatDate(d.parseISODate('2024-12-31')), '2024-12-31'));
                test('addDays does not mutate', () => {
                  const a = d.parseISODate('2024-01-31');
                  const b = d.addDays(a, 1);
                  assert.strictEqual(d.formatDate(a), '2024-01-31');
                  assert.strictEqual(d.formatDate(b), '2024-02-01');
                });
                test('addDays negative', () => assert.strictEqual(d.formatDate(d.addDays(d.parseISODate('2024-03-01'), -1)), '2024-02-29'));
                test('daysBetween', () => assert.strictEqual(d.daysBetween(d.parseISODate('2024-02-27'), d.parseISODate('2024-03-03')), 5));
                test('invalid input still throws', () => assert.throws(() => d.parseISODate('2024/03/15'), /INVALID_DATE/));
                """));
            var (exit, output) = await ctx.Node("--test", "__hidden.test.js");
            File.Delete(ctx.Path("__hidden.test.js"));
            if (exit != 0) failures.Add("скрытые проверки dates.js не прошли: " + Tail(output, 600));
            return failures;
        },
        Allowed: (p, _) => OnlyFiles(p, "src/dates.js", "report.js"),
        MaxChangedLines: 8,
        Solve: dir =>
        {
            var text = ReadIn(dir, "src/dates.js")
                .Replace("new Date(Number(y), Number(mo), Number(d))", "new Date(Number(y), Number(mo) - 1, Number(d))")
                .Replace("date.setDate(date.getDate() + days);\n  return date;", "const next = new Date(date.getTime());\n  next.setDate(next.getDate() + days);\n  return next;");
            WriteIn(dir, "src/dates.js", text);
        });

    // ---------------------------------------------------------------- 7. сохранение CRLF и табов

    private static EvalTask PreserveCrlfAndTabs() => new(
        Id: "preserve-crlf-tabs",
        Title: "Правка файла с CRLF и табами: стиль должен сохраниться",
        Prompt: "В src/legacy.js функция retry делает 3 попытки подряд. Сделай её асинхронной (возвращает промис), 5 попыток и паузой delayMs между попытками (второй параметр, по умолчанию 100 мс). После последней неудачной попытки пробрасывай последнюю ошибку. Сохрани стиль файла как есть.",
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["src/legacy.js"] = Crlf(Tabs(F("""
                'use strict';

                // Legacy helper. Keep the file style: tabs and CRLF line endings.
                function retry(fn) {
                    const attempts = 3;
                    let lastError;
                    for (let i = 0; i < attempts; i++) {
                        try {
                            return fn(i);
                        } catch (e) {
                            lastError = e;
                        }
                    }
                    throw lastError;
                }

                module.exports = { retry };
                """))),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            var text = ctx.Exists("src/legacy.js") ? ctx.Read("src/legacy.js") : string.Empty;
            if (Regex.IsMatch(text, @"(?<!\r)\n")) failures.Add("в файле появились переводы строки без CR (CRLF не сохранён)");
            if (Regex.IsMatch(text, @"(?m)^ +\S")) failures.Add("в файле появились отступы пробелами вместо табов");
            if (!text.Contains("100")) failures.Add("нет значения по умолчанию 100 мс");

            ctx.Write("__hidden.test.js", F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const { retry } = require('./src/legacy');

                function flaky(failures) {
                  let calls = 0;
                  const fn = () => {
                    calls++;
                    if (calls <= failures) throw new Error('boom' + calls);
                    return 'ok' + calls;
                  };
                  return { fn, calls: () => calls };
                }

                test('returns a promise', () => {
                  const p = retry(() => 1, 1);
                  assert.ok(p && typeof p.then === 'function');
                  return p;
                });
                test('succeeds on the 5th attempt', async () => {
                  const f = flaky(4);
                  assert.strictEqual(await retry(f.fn, 1), 'ok5');
                  assert.strictEqual(f.calls(), 5);
                });
                test('rejects with the last error after 5 attempts', async () => {
                  const f = flaky(10);
                  await assert.rejects(() => retry(f.fn, 1), /boom5/);
                  assert.strictEqual(f.calls(), 5);
                });
                test('waits between attempts', async () => {
                  const f = flaky(4);
                  const started = Date.now();
                  await retry(f.fn, 30);
                  assert.ok(Date.now() - started >= 100, 'expected about 4 x 30 ms of delay');
                });
                """));
            var (exit, output) = await ctx.Node("--test", "__hidden.test.js");
            File.Delete(ctx.Path("__hidden.test.js"));
            if (exit != 0) failures.Add("скрытые проверки retry не прошли: " + Tail(output, 600));
            return failures;
        },
        Allowed: (p, _) => OnlyFiles(p, "src/legacy.js"),
        MaxChangedLines: 18,
        Solve: dir => WriteIn(dir, "src/legacy.js", Crlf(Tabs(F("""
            'use strict';

            // Legacy helper. Keep the file style: tabs and CRLF line endings.
            async function retry(fn, delayMs = 100) {
                const attempts = 5;
                let lastError;
                for (let i = 0; i < attempts; i++) {
                    try {
                        return fn(i);
                    } catch (e) {
                        lastError = e;
                        if (i < attempts - 1) await new Promise((resolve) => setTimeout(resolve, delayMs));
                    }
                }
                throw lastError;
            }

            module.exports = { retry };
            """)))));

    // ---------------------------------------------------------------- 8. правила проекта (CONEXY.md)

    private static EvalTask FollowProjectRules() => new(
        Id: "follow-project-rules",
        Title: "Соблюсти правила проекта из CONEXY.md (заголовок файла, отступы, тесты)",
        Prompt: "Добавь функцию clamp(value, min, max) в новый файл src/clamp.js и тесты к ней (минимум 3 кейса) в новом файле test/clamp.test.js. Если min > max, функция бросает Error('INVALID_RANGE').",
        Files: new()
        {
            ["package.json"] = F(PackageJson),
            ["CONEXY.md"] = F("""
                # Правила проекта

                - Каждый новый .js-файл начинается со строки `// @conexy-generated`.
                - Отступ — 2 пробела, табы запрещены.
                - Тесты — только `node:test`, без внешних зависимостей.
                - Код на CommonJS (`module.exports`).
                """),
            ["src/sum.js"] = F("""
                // @conexy-generated
                'use strict';

                function sum(a, b) {
                  return a + b;
                }

                module.exports = { sum };
                """),
            ["test/sum.test.js"] = F("""
                // @conexy-generated
                const test = require('node:test');
                const assert = require('node:assert');
                const { sum } = require('../src/sum');

                test('sum', () => assert.strictEqual(sum(1, 2), 3));
                """),
        },
        Verify: async ctx =>
        {
            var failures = new List<string>();
            await AddTestRun(ctx, failures);

            var newJs = Directory.EnumerateFiles(ctx.Dir, "*.js", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(ctx.Dir, f).Replace('\\', '/'))
                .Where(f => !ctx.Original.ContainsKey(f) && !f.StartsWith("node_modules/", StringComparison.Ordinal))
                .ToList();
            if (!newJs.Contains("src/clamp.js")) failures.Add("нет src/clamp.js");
            if (!newJs.Any(f => f.StartsWith("test/", StringComparison.Ordinal))) failures.Add("нет нового файла тестов в test/");
            foreach (var rel in newJs)
            {
                var text = ctx.Read(rel);
                var first = text.Replace("\r", string.Empty).Split('\n')[0].Trim();
                if (first != "// @conexy-generated") failures.Add($"{rel}: нет заголовка `// @conexy-generated` первой строкой");
                if (text.Contains('\t')) failures.Add($"{rel}: табы вместо пробелов");
            }
            if (ctx.Exists("test/clamp.test.js") && Regex.Matches(ctx.Read("test/clamp.test.js"), @"\btest\(").Count < 3)
                failures.Add("в test/clamp.test.js меньше 3 тестов");
            foreach (var rel in new[] { "src/sum.js", "test/sum.test.js", "CONEXY.md" })
                if (!ctx.Unchanged(rel)) failures.Add($"изменён существующий файл {rel}");

            ctx.Write("__hidden.test.js", F("""
                const test = require('node:test');
                const assert = require('node:assert');
                const { clamp } = require('./src/clamp');
                test('above', () => assert.strictEqual(clamp(5, 0, 3), 3));
                test('below', () => assert.strictEqual(clamp(-1, 0, 3), 0));
                test('inside', () => assert.strictEqual(clamp(2, 0, 3), 2));
                test('edges', () => { assert.strictEqual(clamp(0, 0, 3), 0); assert.strictEqual(clamp(3, 0, 3), 3); });
                test('invalid range', () => assert.throws(() => clamp(1, 5, 2), /INVALID_RANGE/));
                """));
            var (exit, output) = await ctx.Node("--test", "__hidden.test.js");
            File.Delete(ctx.Path("__hidden.test.js"));
            if (exit != 0) failures.Add("скрытые проверки clamp не прошли: " + Tail(output, 600));
            return failures;
        },
        Allowed: (p, kind) => kind == "added" && (p.StartsWith("src/", StringComparison.Ordinal) || p.StartsWith("test/", StringComparison.Ordinal)),
        MaxChangedLines: null,
        Solve: dir =>
        {
            WriteIn(dir, "src/clamp.js", F("""
                // @conexy-generated
                'use strict';

                function clamp(value, min, max) {
                  if (min > max) throw new Error('INVALID_RANGE');
                  return Math.min(Math.max(value, min), max);
                }

                module.exports = { clamp };
                """));
            WriteIn(dir, "test/clamp.test.js", F("""
                // @conexy-generated
                const test = require('node:test');
                const assert = require('node:assert');
                const { clamp } = require('../src/clamp');

                test('above', () => assert.strictEqual(clamp(5, 0, 3), 3));
                test('below', () => assert.strictEqual(clamp(-1, 0, 3), 0));
                test('invalid', () => assert.throws(() => clamp(1, 5, 2), /INVALID_RANGE/));
                """));
        });
}

// ====================================================================== ad-hoc: skills from a folder

// ADHOC: в продукте скиллы лежат в БД пользователя. На стенде их берём из папки проекта
// (ConexyAI/.agents/skills/*/SKILL.md), чтобы проверять, как агент следует выданному скиллу.
internal sealed class FolderSkillService : ISkillService
{
    private readonly IReadOnlyList<(string Name, string Description, string Content)> _skills;

    public FolderSkillService(string skillsDir)
    {
        var list = new List<(string, string, string)>();
        foreach (var dir in Directory.EnumerateDirectories(skillsDir))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) continue;
            var parsed = SkillService.Parse(Path.GetFileName(dir) + ".md", File.ReadAllText(file));
            list.Add((parsed.Name, parsed.Description, parsed.Content));
        }
        _skills = list;
    }

    public Task<IReadOnlyList<SkillSummary>> EnabledSummariesAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkillSummary>>(_skills.Select(s => new SkillSummary(s.Name, s.Description)).ToList());

    public Task<string?> ReadAsync(Guid userId, string name, CancellationToken ct = default) =>
        Task.FromResult(_skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { Content: { } content }
            ? content
            : null);

    public Task<IReadOnlyList<SkillDto>> ListAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SkillDto> UpsertFromFileAsync(Guid userId, string fileName, string text, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SkillDto?> SetEnabledAsync(Guid userId, Guid id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class NoWorkerRunner : IConexyAgentRunner
{
    public Task<string> RunLoopAsync(ConexyJob job, ConversationContext context, CancellationToken ct = default) =>
        Task.FromResult(string.Empty);

    public string GetSystemPrompt(ConexyModelType modelType) => string.Empty;

    public Task<string> RunWorkerAsync(ConexyJob job, AgentIdentity identity, string brief, CancellationToken ct = default) =>
        Task.FromResult("(на стенде нет настоящих агентов-помощников — работай сам)");

    public string PartialOutput => string.Empty;
}
