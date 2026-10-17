using System.Diagnostics;

namespace ConexyAI.Desktop;

/// <summary>
/// Самообновление: версию берём с сервера (он знает последний релиз), exe скачиваем по ссылке с сервера.
/// Запущенный exe Windows переименовать можно, а перезаписать нельзя — поэтому старый уходит в .old,
/// а новый встаёт на его место и запускается с теми же аргументами.
/// </summary>
public static class Updater
{
    public const string SkipFlag = "--no-update";

    private static string? ExePath =>
        Environment.ProcessPath is { } p && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? p
            : null;

    public static void CleanupOld()
    {
        try
        {
            if (ExePath is { } exe && File.Exists(exe + ".old"))
                File.Delete(exe + ".old");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Информация о новой версии или null: новее нет, сервер молчит или запуск не из exe.</summary>
    public static async Task<ClientInfo?> CheckAsync(ServerClient server)
    {
        if (ExePath is null)
            return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var info = await server.GetClientInfoAsync(cts.Token);
            if (info?.Version is null || info.DownloadUrl is null)
                return null;
            return Version.TryParse(info.Version, out var latest)
                   && Version.TryParse(Ui.Version, out var current)
                   && latest > current
                ? info
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Служебный вход для проверки докачки: conexy downloadtest &lt;url&gt; &lt;файл&gt; &lt;версия&gt;.</summary>
    public static Task<bool> TestDownload(string url, string target, string version) => DownloadResumableAsync(url, target, version);

    /// <summary>Служебный вход для проверки скрипта замены: conexy updatetest &lt;цель&gt; &lt;новый файл&gt;.</summary>
    public static int? TestSchedule(string target, string fresh) => ScheduleReplace(target, fresh, [], "test");

    private static int? ScheduleReplace(string exe, string fresh, string[] args, string? version)
    {
        if (!OperatingSystem.IsWindows())
        {
            Ui.Error($"Не удалось заменить файл. Новая версия лежит рядом: {fresh}");
            return null;
        }

        exe = Path.GetFullPath(exe);
        fresh = Path.GetFullPath(fresh);
        string Quote(string a) => "\"" + a.Replace("\"", "\\\"") + "\"";
        var launchArgs = string.Join(' ', args.Append(SkipFlag).Select(Quote));
        var script = """
            @echo off
            chcp 65001 >nul
            set /a n=0
            :retry
            move /y "{FRESH}" "{EXE}" >nul 2>&1
            if not errorlevel 1 goto done
            set /a n+=1
            if %n% geq 60 goto fail
            ping -n 2 127.0.0.1 >nul
            goto retry
            :done
            start "" "{EXE}" {ARGS}
            goto end
            :fail
            echo Could not replace the file. The new version is here: {FRESH}
            pause
            :end
            (goto) 2>nul & del "%~f0"
            """
            .Replace("{FRESH}", fresh).Replace("{EXE}", exe).Replace("{ARGS}", launchArgs);

        var scriptPath = Path.Combine(Path.GetTempPath(), "conexy-update-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(scriptPath, script.Replace("\r\n", "\n").Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", scriptPath },
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Console.WriteLine($"Версия v{version} скачана. Окно закроется, через несколько секунд откроется новая.");
        return 0;
    }

    private const int MaxAttempts = 20;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Скачивание с докачкой. Соединение с GitHub из России часто рвётся на середине 80-мегабайтного файла
    /// («unexpected EOF», «response ended prematurely»), и скачивание с нуля не доходило до конца. Теперь при обрыве
    /// запрашиваем остаток (HTTP Range), а частичный файл переживает и перезапуск клиента (помечен версией).
    /// </summary>
    private static async Task<bool> DownloadResumableAsync(string url, string target, string version)
    {
        var marker = target + ".version";
        if (File.Exists(target) && (!File.Exists(marker) || File.ReadAllText(marker).Trim() != version))
            File.Delete(target); // кусок другой версии — докачивать нельзя
        File.WriteAllText(marker, version);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // зависание ловит IdleTimeout на каждом чтении
        };
        long total = 0;
        var lastShown = -1;
        for (var attempt = 1; ; attempt++)
        {
            var have = File.Exists(target) ? new FileInfo(target).Length : 0;
            if (total > 0 && have >= total)
                break;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
                if (have > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Файл уже целиком (или кусок испорчен) — начинаем заново, если размер не известен.
                    if (total > 0 && have == total)
                        break;
                    File.Delete(target);
                    continue;
                }
                response.EnsureSuccessStatusCode();

                // Сервер проигнорировал Range и отдаёт файл с начала — пишем тоже с начала.
                var resumed = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resumed)
                    have = 0;
                total = response.Content.Headers.ContentRange?.Length
                        ?? (response.Content.Headers.ContentLength is { } len ? len + have : 0);

                await using var source = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(target, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                var done = have;
                while (true)
                {
                    using var idle = new CancellationTokenSource(IdleTimeout);
                    var read = await source.ReadAsync(buffer, idle.Token);
                    if (read == 0)
                        break;
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0 && (int)(done * 100 / total) is var pct && pct != lastShown)
                    {
                        lastShown = pct;
                        Console.Write($"\r  {pct}%   ");
                    }
                }
                if (total == 0 || done >= total)
                    break;
                throw new IOException("соединение закрылось раньше конца файла");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && attempt < MaxAttempts)
            {
                var wait = TimeSpan.FromSeconds(Math.Min(10, attempt * 2));
                Console.Write($"\r  {Math.Max(lastShown, 0)}%  обрыв соединения, продолжаю ({attempt}/{MaxAttempts - 1})…   ");
                await Task.Delay(wait);
            }
        }
        Console.WriteLine();

        // Проверка целостности: нужный размер и заголовок exe (MZ), чтобы не подменить рабочую программу обрывком.
        var size = new FileInfo(target).Length;
        var header = new byte[2];
        await using (var check = File.OpenRead(target))
            _ = await check.ReadAsync(header);
        if ((total > 0 && size != total) || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            File.Delete(target);
            Ui.Error($"Скачанный файл повреждён ({size} из {total} байт) — повторите /update.");
            return false;
        }
        File.Delete(marker);
        return true;
    }

    /// <summary>Скачивает и подменяет exe, затем запускает новую версию и ждёт её завершения. Возвращает её код выхода.</summary>
    public static async Task<int?> ApplyAsync(ClientInfo info, string[] args)
    {
        var exe = ExePath;
        if (exe is null || info.DownloadUrl is null)
            return null;

        var fresh = exe + ".new";
        try
        {
            Console.WriteLine($"Скачиваю v{info.Version}…");
            if (!await DownloadResumableAsync(info.DownloadUrl, fresh, info.Version ?? "?"))
                return null;

            try
            {
                File.Move(exe, exe + ".old", overwrite: true);
                File.Move(fresh, exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Запущенный exe нельзя подменить на лету (антивирус, блокировка проводника, старый .old занят): возвращаем
                // как было и передаём замену вспомогательному скрипту: он дождётся выхода этой программы.
                if (!File.Exists(exe) && File.Exists(exe + ".old"))
                    File.Move(exe + ".old", exe);
                return ScheduleReplace(exe, fresh, args, info.Version);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Скачанную часть не удаляем: следующий /update докачает её с того же места.
            Ui.Error("Не удалось обновиться: " + ex.Message + " — повторите /update, скачивание продолжится с того же места.");
            return null;
        }

        Console.WriteLine($"Обновлено до v{info.Version}. Запускаю…\n");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(SkipFlag);
        using var child = Process.Start(psi);
        if (child is null)
            return 0;
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
}
