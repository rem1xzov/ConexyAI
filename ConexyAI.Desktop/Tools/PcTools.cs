using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ConexyAI.Desktop.Tools;

/// <summary>
/// Инструменты ПК за пределами рабочей папки: фоновые процессы (dev-серверы, БД, docker), HTTP к localhost,
/// скриншот страницы в браузере ПК, чтение файлов по всему диску (только чтение), копирование в проект,
/// скачивание и установка программ. Всё, что меняет систему или читает вне проекта, — с подтверждением.
/// </summary>
public sealed class PcTools : IDisposable
{
    private const int MaxHttpBody = 20_000;
    private const int MaxReadChars = 60_000;
    private const int MaxLogLines = 3000;

    private static readonly StringComparison Cmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly PathGuard _guard;
    private readonly ICommandApprover _approver;
    private readonly ShellTool _shell;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Папки, чтение которых пользователь уже разрешил в этой сессии (вложенные — тоже).
    private readonly HashSet<string> _readableRoots = new(StringComparer.OrdinalIgnoreCase);
    // Внешние хосты, запросы к которым пользователь уже разрешил в этой сессии.
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Background> _background = new();
    private int _nextId;

    private sealed class Background
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Command { get; init; }
        public required Process Process { get; init; }
        public ConcurrentQueue<string> Lines { get; } = new();
        public DateTime Started { get; } = DateTime.Now;

        public void Add(string? line)
        {
            if (line is null) return;
            Lines.Enqueue(line);
            while (Lines.Count > MaxLogLines && Lines.TryDequeue(out _)) { }
        }
    }

    public PcTools(PathGuard guard, ICommandApprover approver, ShellTool shell)
    {
        _guard = guard;
        _approver = approver;
        _shell = shell;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    private async Task Require(string title, string details, CancellationToken ct)
    {
        if (!await _approver.ApproveAsync(title, details, _guard.Root, ct))
            throw new ToolException("Пользователь отклонил действие. Не повторяй его; предложи другой путь или спроси, что делать.");
    }

    // ── Фоновые процессы ────────────────────────────────────────────────────

    public async Task<ToolResult> BgStartAsync(ToolArgs args, CancellationToken ct)
    {
        var command = args.Required("command", "bg_start");
        if (CommandPolicy.ForbiddenReason(command) is { } reason)
            return ToolResult.Fail(reason);

        var cwd = args.Str("cwd") is { Length: > 0 } dir ? _guard.Resolve(dir) : _guard.Root;
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Папка не найдена: {cwd}");

        await Require("Фоновый процесс", command, ct);

        var id = "bg" + Interlocked.Increment(ref _nextId);
        var psi = ShellTool.CreateShell(command, useCmd: false, wrapExitCode: false);
        psi.WorkingDirectory = cwd;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var bg = new Background { Id = id, Name = args.Str("name") ?? id, Command = command, Process = process };
        process.OutputDataReceived += (_, e) => bg.Add(e.Data);
        process.ErrorDataReceived += (_, e) => bg.Add(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("Не удалось запустить: " + ex.Message);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _background[id] = bg;

        var wait = Math.Clamp(args.Int("wait_seconds") ?? 8, 1, 60);
        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            waitCts.CancelAfter(TimeSpan.FromSeconds(wait));
            try { await process.WaitForExitAsync(waitCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        var status = process.HasExited ? $"завершился с кодом {process.ExitCode}" : "работает";
        return new ToolResult(
            $"id={id} ({bg.Name}) — {status}. Логи: bg_logs id={id}, остановка: bg_stop id={id}.\n" + Tail(bg, 60),
            process.HasExited && process.ExitCode != 0);
    }

    public ToolResult BgLogs(ToolArgs args)
    {
        var id = args.Str("id");
        if (string.IsNullOrEmpty(id))
        {
            if (_background.IsEmpty)
                return ToolResult.Ok("Фоновых процессов нет.");
            return ToolResult.Ok(string.Join('\n', _background.Values.OrderBy(b => b.Started).Select(b =>
                $"{b.Id} ({b.Name}) — {(b.Process.HasExited ? $"завершён, код {b.Process.ExitCode}" : "работает")} — {b.Command}")));
        }

        if (!_background.TryGetValue(id, out var bg))
            return ToolResult.Fail($"Нет фонового процесса {id}.");
        var status = bg.Process.HasExited ? $"завершён, код {bg.Process.ExitCode}" : "работает";
        return ToolResult.Ok($"{bg.Id} ({bg.Name}) — {status}\n" + Tail(bg, Math.Clamp(args.Int("lines") ?? 80, 1, 1000)));
    }

    public ToolResult BgStop(ToolArgs args)
    {
        var id = args.Required("id", "bg_stop");
        if (!_background.TryRemove(id, out var bg))
            return ToolResult.Fail($"Нет фонового процесса {id}.");
        try
        {
            if (!bg.Process.HasExited)
                bg.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        return ToolResult.Ok($"{id} ({bg.Name}) остановлен.\n" + Tail(bg, 20));
    }

    private static string Tail(Background bg, int lines)
    {
        var all = bg.Lines.ToArray();
        var text = string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        return text.Length > 20_000 ? text[^20_000..] : text;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    public async Task<ToolResult> HttpAsync(ToolArgs args, CancellationToken ct)
    {
        var url = args.Required("url", "http_request");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ToolResult.Fail("Нужен абсолютный http(s) URL, например http://localhost:5000/health.");

        var method = (args.Str("method") ?? "GET").ToUpperInvariant();
        // К localhost — свободно (проверка своих сервисов). Во внешний интернет — с подтверждением хоста:
        // иначе письмо/документ с внедрённой инструкцией может вывести данные с ПК без ведома пользователя.
        if (!uri.IsLoopback)
        {
            bool known;
            lock (_allowedHosts) known = _allowedHosts.Contains(uri.Host);
            if (!known)
            {
                await Require("HTTP-запрос в интернет", $"{method} {url}\n(разрешение действует на {uri.Host} до конца сессии)", ct);
                lock (_allowedHosts) _allowedHosts.Add(uri.Host);
            }
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var headers = args.Dict("headers");
        if (args.Str("body") is { } body)
            request.Content = new StringContent(body, Encoding.UTF8,
                headers.TryGetValue("Content-Type", out var type) ? type : "application/json");
        foreach (var (name, value) in headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var sb = new StringBuilder();
            sb.AppendLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} · {watch.ElapsedMilliseconds} мс");
            foreach (var h in response.Headers.Concat(response.Content.Headers).Take(15))
                sb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
            sb.AppendLine();
            sb.Append(text.Length > MaxHttpBody ? text[..MaxHttpBody] + $"\n[… обрезано, всего {text.Length} символов]" : text);
            return new ToolResult(sb.ToString(), (int)response.StatusCode >= 500);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail($"Запрос не прошёл: {ex.Message}. Если это localhost — сервис ещё не поднялся или слушает другой порт (смотри bg_logs).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Fail("Таймаут 30 с — сервис не ответил.");
        }
    }

    // ── Скриншот страницы в браузере ПК ─────────────────────────────────────

    private static string? FindBrowser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates = OperatingSystem.IsWindows()
            ?
            [
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Chromium\Application\chrome.exe"),
            ]
            :
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge",
            ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<ToolResult> ScreenshotAsync(ToolArgs args, CancellationToken ct)
    {
        var url = args.Required("url", "page_screenshot");
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var file = _guard.Resolve(url);
            if (!File.Exists(file))
                return ToolResult.Fail($"Файл не найден: {url}");
            url = new Uri(file).AbsoluteUri;
        }

        var browser = FindBrowser();
        if (browser is null)
            return ToolResult.Fail("На ПК не найден Edge или Chrome. Проверь страницу через http_request или установи браузер (install_software Google.Chrome).");

        var width = Math.Clamp(args.Int("width") ?? 1280, 320, 2560);
        var height = Math.Clamp(args.Int("height") ?? 800, 240, 2400);
        var temp = Path.Combine(Path.GetTempPath(), "conexy-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var png = Path.Combine(temp, "shot.png");

        var psi = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[]
                 {
                     "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", $"--user-data-dir={Path.Combine(temp, "profile")}", $"--window-size={width},{height}",
                     "--virtual-time-budget=8000", $"--screenshot={png}", url,
                 })
            psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi)!;
            _ = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return ToolResult.Fail("Браузер не успел отрисовать страницу за 60 с.");
            }

            if (!File.Exists(png))
                return ToolResult.Fail("Браузер не создал скриншот. Проверь, что адрес открывается (http_request).");
            var bytes = await File.ReadAllBytesAsync(png, ct);
            return ToolResult.Ok("IMAGE_BASE64:image/png:" + Convert.ToBase64String(bytes));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── Файлы по всему ПК (только чтение) ───────────────────────────────────

    private static string Absolute(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.StartsWith('~'))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('\\', '/'));
        if (!Path.IsPathRooted(path))
            throw new ToolException("Нужен абсолютный путь (например C:\\Users\\me\\AppData\\Local\\app\\log.txt). Для файлов проекта используй file_read.");
        return Path.GetFullPath(path);
    }

    private bool IsInsideProject(string full) =>
        full.Equals(_guard.Root, Cmp) || full.StartsWith(_guard.Root + Path.DirectorySeparatorChar, Cmp);

    private async Task EnsureReadable(string full, bool isDirectory, CancellationToken ct)
    {
        if (IsInsideProject(full))
            return;
        // Ключи и секреты не читаем ни с каким подтверждением.
        if (CommandPolicy.ForbiddenReason(full) is not null)
            throw new ToolException("Чтение этого места запрещено политикой (ключи, учётные данные).");

        var folder = isDirectory ? full : Path.GetDirectoryName(full) ?? full;
        lock (_readableRoots)
        {
            if (_readableRoots.Any(r => folder.Equals(r, Cmp) || folder.StartsWith(r + Path.DirectorySeparatorChar, Cmp)))
                return;
        }

        await Require("Чтение вне проекта", $"{full}\n(разрешение действует на папку {folder} до конца сессии)", ct);
        lock (_readableRoots)
            _readableRoots.Add(folder);
    }

    public async Task<ToolResult> ListDirAsync(ToolArgs args, CancellationToken ct)
    {
        var dir = Absolute(args.Required("path", "pc_list_dir"));
        if (!Directory.Exists(dir))
            return ToolResult.Fail($"Папка не найдена: {dir}");
        await EnsureReadable(dir, isDirectory: true, ct);

        var pattern = args.Str("pattern") is { Length: > 0 } p ? p : "*";
        var sb = new StringBuilder(dir).AppendLine();
        var count = 0;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir, "*").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (++count > 500) break;
                sb.AppendLine($"  [dir]  {Path.GetFileName(d)}/");
            }
            foreach (var f in Directory.EnumerateFiles(dir, pattern).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (++count > 500) { sb.AppendLine("  … (обрезано на 500)"); break; }
                var info = new FileInfo(f);
                sb.AppendLine($"  {info.Length,10:N0}  {info.LastWriteTime:yyyy-MM-dd HH:mm}  {info.Name}");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Fail("Нет доступа: " + ex.Message);
        }
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    public async Task<ToolResult> ReadFileAsync(ToolArgs args, CancellationToken ct)
    {
        var file = Absolute(args.Required("path", "pc_read_file"));
        if (!File.Exists(file))
            return ToolResult.Fail($"Файл не найден: {file}");
        await EnsureReadable(file, isDirectory: false, ct);

        // Логи часто держит открытыми их программа — читаем с общим доступом.
        string[] lines;
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var text = await reader.ReadToEndAsync(ct);
            if (text.Contains('\0'))
                return ToolResult.Fail("Файл бинарный — текст не прочитать. Скопируй его в проект (pc_copy_to_project), если он нужен.");
            lines = text.Replace("\r\n", "\n").Split('\n');
        }

        IEnumerable<(string Line, int No)> selected = lines.Select((l, i) => (l, i + 1));
        if (args.Int("tail_lines") is { } tail and > 0)
            selected = selected.Skip(Math.Max(0, lines.Length - tail));
        else
            selected = selected.Skip(Math.Max(0, (args.Int("offset") ?? 1) - 1)).Take(Math.Clamp(args.Int("limit") ?? 2000, 1, 10000));

        var sb = new StringBuilder();
        foreach (var (line, no) in selected)
        {
            sb.Append(no.ToString().PadLeft(6)).Append('\t').AppendLine(line);
            if (sb.Length > MaxReadChars)
            {
                sb.AppendLine($"[… обрезано; всего строк: {lines.Length}. Используй offset/limit или tail_lines]");
                break;
            }
        }
        return ToolResult.Ok($"{file} ({lines.Length} строк)\n{sb}");
    }

    public async Task<ToolResult> CopyToProjectAsync(ToolArgs args, CancellationToken ct)
    {
        var source = Absolute(args.Required("source", "pc_copy_to_project"));
        var destination = _guard.ResolveForWrite(args.Required("destination", "pc_copy_to_project"));
        var isDir = Directory.Exists(source);
        if (!isDir && !File.Exists(source))
            return ToolResult.Fail($"Не найдено: {source}");
        if (CommandPolicy.ForbiddenReason(source) is not null)
            return ToolResult.Fail("Копирование этого места запрещено политикой (ключи, учётные данные).");

        await Require("Копирование в проект", $"{source}\n→ {_guard.Relative(destination)}", ct);

        if (isDir)
        {
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                if (++copied > 5000)
                    return ToolResult.Fail("Слишком много файлов (> 5000) — скопировано частично.");
            }
            return ToolResult.Ok($"Скопировано файлов: {copied} → {_guard.Relative(destination)}");
        }

        if (Directory.Exists(destination))
            destination = Path.Combine(destination, Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        return ToolResult.Ok($"Скопировано → {_guard.Relative(destination)} ({new FileInfo(destination).Length:N0} байт)");
    }

    // ── Скачивание и установка ──────────────────────────────────────────────

    public async Task<ToolResult> DownloadAsync(ToolArgs args, CancellationToken ct)
    {
        var url = args.Required("url", "download_file");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ToolResult.Fail("Нужен http(s) URL.");

        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
        string target;
        if (args.Str("destination") is { Length: > 0 } dest)
        {
            target = _guard.ResolveForWrite(dest);
            if (Directory.Exists(target) || dest.EndsWith('/') || dest.EndsWith('\\'))
                target = Path.Combine(target, name);
        }
        else
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            target = Path.Combine(downloads, name);
        }

        await Require("Скачивание", $"{url}\n→ {target}", ct);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return ToolResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(target))
            await src.CopyToAsync(dst, ct);
        return ToolResult.Ok($"Скачано: {target} ({new FileInfo(target).Length:N0} байт)");
    }

    public async Task<ToolResult> InstallAsync(ToolArgs args, CancellationToken ct)
    {
        var package = args.Required("package", "install_software").Trim();
        if (package.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
            return ToolResult.Fail("Некорректный id пакета.");
        if (!OperatingSystem.IsWindows())
            return ToolResult.Fail("install_software работает через winget (Windows). На этой ОС установи через пакетный менеджер командой bash (с подтверждением).");

        await Require("Установка программы", $"winget install --id {package}", ct);
        var result = await _shell.RunApprovedAsync(
            $"winget install --id {package} -e --accept-source-agreements --accept-package-agreements --disable-interactivity",
            900, ct);
        if (result.Output.Contains("winget") && result.Output.Contains("не распознано", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail("winget не найден на этом ПК. Используй download_file с официальным установщиком (затем запуск — с подтверждением).");
        return new ToolResult(result.Output + "\nPATH обновляется автоматически: новые команды доступны в следующих вызовах bash.", result.IsError);
    }

    public void Dispose()
    {
        foreach (var bg in _background.Values)
        {
            try
            {
                if (!bg.Process.HasExited)
                    bg.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }
        _background.Clear();
        _http.Dispose();
    }
}
