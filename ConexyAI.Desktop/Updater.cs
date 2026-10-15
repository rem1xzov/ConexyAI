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
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;

            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(fresh))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                var lastShown = -1;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0 && (int)(done * 100 / total) is var pct && pct != lastShown)
                    {
                        lastShown = pct;
                        Console.Write($"\r  {pct}%   ");
                    }
                }
            }
            Console.WriteLine();

            File.Move(exe, exe + ".old", overwrite: true);
            File.Move(fresh, exe);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Ui.Error("Не удалось обновиться: " + ex.Message);
            try { File.Delete(fresh); } catch (IOException) { }
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
