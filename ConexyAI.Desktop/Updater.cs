using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ConexyAI.Desktop;

/// <summary>
/// Самообновление: версию берём с сервера (он знает последний релиз), exe скачиваем по ссылке с сервера.
/// Запущенный exe Windows переименовать можно, а перезаписать нельзя — поэтому старый уходит в .old,
/// а новый встаёт на его место и запускается с теми же аргументами.
/// </summary>
public static class Updater
{
    public const string SkipFlag = "--no-update";

    // UPDATE_INTEGRITY: ссылку на exe даёт сервер, а серверу мы не доверяем. Принимаем только https с GitHub
    // и сверяем SHA-256 с digest ассета релиза из GitHub API (независимый от сервера источник). Нет digest или
    // не совпал — обновление отклоняется. Защита от взлома самого репозитория GitHub это не даёт.
    private static readonly string[] AllowedDownloadHosts =
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    private const string ReleasesApi = "https://api.github.com/repos/rem1xzov/ConexyAI/releases?per_page=20";
    // ASSET_NAME: файл релиза называется так же, как сам клиент — conexy.exe (раньше conexy-win-x64.exe).
    private const string AssetName = "conexy.exe";
    private const string ReleasesPage = "https://github.com/rem1xzov/ConexyAI/releases";
    private const int MaxRedirects = 5;

    internal static bool IsAllowedDownloadUrl(Uri? uri) => IsAllowedDownloadUrl(uri, signedManifest: false);

    /// <summary>
    /// Для подписанного манифеста доверие даёт подпись, а не хост: разрешаем любой https на 443
    /// (своё зеркало, CDN, S3). Для GitHub-фолбэка — только известные хосты.
    /// </summary>
    internal static bool IsAllowedDownloadUrl(Uri? uri, bool signedManifest) =>
        uri is { IsAbsoluteUri: true, Scheme: "https" } && uri.IsDefaultPort
        && (signedManifest || AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Ожидаемый SHA-256 (hex) ассета с таким <c>browser_download_url</c> или null, если такого ассета/digest нет.
    /// <para>
    /// ASSET_BY_URL: сверяем по ССЫЛКЕ, а не по имени файла. Имя ассета менялось (conexy-win-x64.exe → conexy.exe),
    /// и жёсткая проверка имени ломала обновление ровно в момент переименования: сервер уже отдаёт новую ссылку,
    /// а старый клиент ищет ассет со старым именем и не находит ничего. Ссылка с GitHub однозначно указывает
    /// на релиз и файл, а контрольная сумма всё равно сверяется.
    /// </para>
    /// </summary>
    private static async Task<string?> ExpectedSha256Async(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ConexyAI-Desktop/" + Ui.Version);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesApi, ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("browser_download_url", out var link) || link.GetString() != url)
                    continue;
                // Имя принимаем любое из conexy*, кроме служебных файлов: у релиза может лежать и новый файл,
                // и старая копия, а RID теперь несколько (win-x64, linux-x64, osx-arm64 …).
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !name.StartsWith("conexy", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
                    continue;
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    return null;
                var hex = digest["sha256:".Length..].ToLowerInvariant();
                return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
            }
        }
        return null;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

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

    /// <summary>
    /// Проверка обновления. Сначала — подписанный манифест на своём домене
    /// (<c>{сервер}/downloads/latest.json</c>), затем — GitHub-релиз с digest из GitHub API.
    /// </summary>
    public static async Task<ClientInfo?> CheckAsync(ServerClient server)
    {
        if (ExePath is null)
            return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var info = await server.GetClientInfoAsync(cts.Token);
            if (!Version.TryParse(Ui.Version, out var currentVersion))
                return null;
            var current = Norm(currentVersion);

            // UPDATE_MIRROR: подписанный манифест важнее того, что ответил сервер.
            try
            {
                if (await MirrorAsync(server.BaseUrl, current, cts.Token) is { } mirror)
                    return mirror;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Зеркала нет / оно недоступно — работаем как раньше, через GitHub.
            }

            if (info?.Version is null || info.DownloadUrl is null)
                return null;
            return Version.TryParse(info.Version, out var latest) && Norm(latest) > current
                ? info
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>0.9.11 и 0.9.11.0 — одна версия: без нормализации Version считает «0.9.11.0» новее «0.9.11».</summary>
    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>
    /// latest.json с зеркала: версия должна быть новее, подпись — валидной, файл для текущего RID —
    /// на https. Ошибка подписи — это отсутствие обновления, а не повод взять что-то другое с того же хоста.
    /// </summary>
    private static async Task<ClientInfo?> MirrorAsync(string baseUrl, Version current, CancellationToken ct)
    {
        var url = Environment.GetEnvironmentVariable("CONEXY_UPDATE_MANIFEST") is { Length: > 0 } custom
            ? custom
            : baseUrl.TrimEnd('/') + "/downloads/latest.json";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var manifestUri) || !IsAllowedDownloadUrl(manifestUri, signedManifest: true))
            return null;

        using var http = CreateHttpClient(TimeSpan.FromSeconds(6));
        var json = await http.GetStringAsync(manifestUri, ct);
        var manifest = UpdateSigning.Parse(json);
        if (manifest is null || !UpdateSigning.Verify(manifest))
            return null;
        if (!Version.TryParse(manifest.Version, out var latest) || Norm(latest) <= current)
            return null;
        if (!manifest.Files.TryGetValue(UpdateSigning.CurrentRid, out var file)
            || !Uri.TryCreate(file.Url, UriKind.Absolute, out var fileUri)
            || !IsAllowedDownloadUrl(fileUri, signedManifest: true)
            || file.Sha256.Length != 64
            || !file.Sha256.All(Uri.IsHexDigit))
            return null;
        return new ClientInfo(file.Url, manifest.Version, Sha256: file.Sha256.ToLowerInvariant());
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), AllowAutoRedirect = true };
        if (NetProxy.FromUrl(ClientConfig.LoadHttpProxy()) is { } proxy)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = timeout };
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
    private static async Task<bool> DownloadResumableAsync(string url, string target, string version, bool signedManifest = false)
    {
        var marker = target + ".version";
        if (File.Exists(target) && (!File.Exists(marker) || File.ReadAllText(marker).Trim() != version))
            File.Delete(target); // кусок другой версии — докачивать нельзя
        File.WriteAllText(marker, version);

        // UPDATE_INTEGRITY: редиректы (github.com → *.githubusercontent.com) вручную, каждый хост по белому списку.
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20), AllowAutoRedirect = false };
        // HTTP_PROXY: скачивание обновлений тоже идёт через прокси, если он задан.
        if (NetProxy.FromUrl(ClientConfig.LoadHttpProxy()) is { } proxy)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        using var http = new HttpClient(handler)
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
                using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var current = new Uri(url);
                HttpResponseMessage? hopResponse = null;
                for (var hop = 0; ; hop++)
                {
                    if (!IsAllowedDownloadUrl(current, signedManifest))
                    {
                        Ui.Error(signedManifest
                            ? $"Обновление отклонено: зеркало перенаправило на небезопасный адрес ({current.Host})."
                            : $"Обновление отклонено: адрес скачивания не с GitHub ({current.Host}).");
                        return false;
                    }
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    if (have > 0)
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                    hopResponse = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
                    if ((int)hopResponse.StatusCode is not (301 or 302 or 303 or 307 or 308) || hopResponse.Headers.Location is not { } location)
                        break;
                    hopResponse.Dispose();
                    if (hop >= MaxRedirects)
                        throw new HttpRequestException("слишком много редиректов");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                }
                using var response = hopResponse!;
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

        // Проверка целостности: размер и сигнатура исполняемого файла текущей ОС.
        var size = new FileInfo(target).Length;
        var header = new byte[4];
        await using (var check = File.OpenRead(target))
            _ = await check.ReadAsync(header);
        if ((total > 0 && size != total) || !LooksLikeExecutable(header))
        {
            File.Delete(target);
            Ui.Error($"Скачанный файл повреждён ({size} из {total} байт) — повторите /update.");
            return false;
        }
        File.Delete(marker);
        return true;
    }

    /// <summary>CROSS_PLATFORM: exe — MZ, Linux — ELF, macOS — Mach-O (тонкий или fat).</summary>
    internal static bool LooksLikeExecutable(ReadOnlySpan<byte> header)
    {
        if (OperatingSystem.IsWindows())
            return header.Length >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        if (OperatingSystem.IsLinux())
            return header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F';
        if (OperatingSystem.IsMacOS())
        {
            if (header.Length < 4)
                return false;
            var magic = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
        }
        return header.Length > 0;
    }

    /// <summary>Скачивает и подменяет exe, затем запускает новую версию и ждёт её завершения. Возвращает её код выхода.</summary>
    public static async Task<int?> ApplyAsync(ClientInfo info, string[] args)
    {
        var exe = ExePath;
        if (exe is null || info.DownloadUrl is null)
            return null;

        var fresh = exe + ".new";
        var signedManifest = info.Sha256 is { Length: 64 } && info.Sha256.All(Uri.IsHexDigit);
        try
        {
            // UPDATE_INTEGRITY: подписанный манифест даёт и ссылку, и SHA-256; иначе — GitHub + digest.
            if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var downloadUri)
                || !IsAllowedDownloadUrl(downloadUri, signedManifest))
            {
                Ui.Error(signedManifest
                    ? "Обновление отклонено: зеркало дало ссылку не по https. Скачайте версию вручную."
                    : "Обновление отклонено: сервер дал ссылку не на релиз GitHub. Скачайте версию вручную со страницы релизов.");
                return null;
            }

            string? expected = signedManifest ? info.Sha256!.ToLowerInvariant() : null;
            if (expected is null)
            {
                try
                {
                    using var apiCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    expected = await ExpectedSha256Async(info.DownloadUrl, apiCts.Token);
                }
                catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
                {
                    Ui.Error("Не удалось проверить обновление через GitHub API: " + ex.Message + ". Обновление пропущено.");
                    return null;
                }
                if (expected is null)
                {
                    Ui.Error($"Обновление отклонено: в релизах GitHub нет {AssetName} с этой ссылкой и контрольной суммой SHA-256. " +
                             $"Скачайте вручную: {ReleasesPage}");
                    return null;
                }
            }

            Console.WriteLine($"Скачиваю v{info.Version}…");
            if (!await DownloadResumableAsync(info.DownloadUrl, fresh, info.Version ?? "?", signedManifest))
                return null;

            if (await Sha256Async(fresh) != expected)
            {
                File.Delete(fresh);
                Ui.Error("Обновление отклонено: SHA-256 скачанного файла не совпал с релизом GitHub. Файл удалён.");
                return null;
            }
            // Если текущий exe подписан Authenticode, новый тоже должен быть подписан. Релизы сейчас без
            // подписи — тогда проверка пропускается и целостность держится на SHA-256.
            if (OperatingSystem.IsWindows() && Authenticode.IsTrusted(exe) && !Authenticode.IsTrusted(fresh))
            {
                File.Delete(fresh);
                Ui.Error("Обновление отклонено: у новой версии нет действительной цифровой подписи.");
                return null;
            }

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

    /// <summary>UPDATE_INTEGRITY: проверка подписи Authenticode через WinVerifyTrust (без UI и без проверки отзыва онлайн).</summary>
    private static class Authenticode
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

        public static bool IsTrusted(string path)
        {
            var filePath = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = filePath };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WintrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 1,       // WTD_STATEACTION_VERIFY
                };
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                return result == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
                Marshal.FreeCoTaskMem(filePath);
            }
        }
    }
}
