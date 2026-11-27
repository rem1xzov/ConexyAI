using System.Runtime.CompilerServices;
using ConexyAI.Desktop;

// VISUAL_DESKTOP: контракт визуального окна, на который завязаны и клиент, и веб-приложение.
// Клиент открывает /desktop в Chromium-режиме приложения и находит окно по заголовку
// "ConexyAI Desktop" — этот же заголовок выставляет SPA (frontend/src/utils/desktopMode.ts).
// Здесь проверяются чистые части: адрес окна с одноразовым кодом входа и согласованные константы.
internal static class VisualDesktopTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("visual desktop: the window opens the /desktop route with a one-time handoff", UrlAsync);
        TestRegistry.Add("visual desktop: title, route and profile are the app-specific contract", ContractAsync);
        TestRegistry.Add("visual desktop: CONEXY_VISUAL_PROFILE_DIR relocates the window profile", ProfileOverrideAsync);
        TestRegistry.Add("visual desktop: CONEXY_HOME relocates client settings and the default startup mode persists", ConsoleHomeAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task UrlAsync()
    {
        Assert(VisualDesktop.BuildUrl("https://conexyai.ru/", null) == "https://conexyai.ru/desktop",
            "the server root is normalised and the /desktop route is appended");
        Assert(VisualDesktop.BuildUrl("https://conexyai.ru", "abc123") == "https://conexyai.ru/desktop?handoff=abc123",
            "the one-time handoff code travels in the query string");
        Assert(VisualDesktop.BuildUrl("http://localhost:5173", "a b&c") == "http://localhost:5173/desktop?handoff=a%20b%26c",
            "the handoff code is URL-escaped");
        Assert(VisualDesktop.BuildUrl(null, null) == VisualDesktop.FallbackUrl + "/desktop",
            "without a configured server the public site is the fallback");
        return Task.CompletedTask;
    }

    private static Task ContractAsync()
    {
        Assert(VisualDesktop.WindowTitle == "ConexyAI Desktop",
            "the window title must stay in sync with VISUAL_DESKTOP_TITLE in the web app");
        Assert(VisualDesktop.Route == "/desktop", "the visual window route is /desktop");
        Assert(VisualDesktop.FallbackUrl.StartsWith("https://", StringComparison.Ordinal),
            "the fallback is the public https site");
        Assert(VisualDesktop.ProfileDir.EndsWith("visual-desktop", StringComparison.OrdinalIgnoreCase),
            "the app window gets its own browser profile");
        Assert(VisualDesktop.ProfileDir.StartsWith(ProjectStore.ConfigDir, StringComparison.OrdinalIgnoreCase),
            "the profile lives with the client's settings, not in the repository");
        Assert(VisualDesktop.IsSupported == OperatingSystem.IsWindows(),
            "the visual desktop window is a Windows-only feature");
        Assert(VisualDesktop.HotkeyLabel == "Ctrl+Alt+Shift+V",
            "the switch hotkey must stay a collision-safe combo (Ctrl+Alt+V is Word's Paste Special)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Профиль окна можно унести с системного диска: пригодится и для портативной установки, и для
    /// прогонов в песочнице, где профиль пользователя недоступен на запись.
    /// </summary>
    private static Task ProfileOverrideAsync()
    {
        var previous = Environment.GetEnvironmentVariable("CONEXY_VISUAL_PROFILE_DIR");
        var custom = Path.Combine(Path.GetTempPath(), "conexy-visual-profile-test");
        try
        {
            Environment.SetEnvironmentVariable("CONEXY_VISUAL_PROFILE_DIR", custom);
            Assert(VisualDesktop.ProfileDir == Path.GetFullPath(custom),
                "the override is used verbatim (normalised) when it is set");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONEXY_VISUAL_PROFILE_DIR", previous);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// CONEXY_HOME переносит все настройки клиента (config.json, чаты, права, профиль окна), а
    /// /visual default решает, что открывать при двойном клике по exe. Проверяем на изолированной
    /// папке, чтобы не трогать реальные настройки пользователя.
    /// </summary>
    private static Task ConsoleHomeAsync()
    {
        var previousHome = Environment.GetEnvironmentVariable("CONEXY_HOME");
        var previousProfile = Environment.GetEnvironmentVariable("CONEXY_VISUAL_PROFILE_DIR");
        var home = Path.Combine(Path.GetTempPath(), "conexy-home-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("CONEXY_HOME", home);
            Environment.SetEnvironmentVariable("CONEXY_VISUAL_PROFILE_DIR", null);
            Directory.CreateDirectory(home);

            Assert(ProjectStore.ConfigDir == Path.GetFullPath(home), "CONEXY_HOME is used as the settings folder");
            Assert(VisualDesktop.ProfileDir == Path.Combine(Path.GetFullPath(home), "visual-desktop"),
                "the window profile follows the relocated settings folder");

            Assert(!ClientConfig.LoadVisualDefault(), "console is the default startup mode until the user changes it");
            ClientConfig.SaveVisualDefault(true);
            Assert(ClientConfig.LoadVisualDefault(), "the visual desktop can be chosen as the default startup mode");
            var saved = File.ReadAllText(Path.Combine(home, "config.json"));
            Assert(saved.Contains("\"VisualDefault\": true", StringComparison.Ordinal),
                "the choice is persisted in the config file");
            ClientConfig.SaveVisualDefault(false);
            Assert(!ClientConfig.LoadVisualDefault(), "the default can be switched back to the console");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONEXY_HOME", previousHome);
            Environment.SetEnvironmentVariable("CONEXY_VISUAL_PROFILE_DIR", previousProfile);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return Task.CompletedTask;
    }
}
