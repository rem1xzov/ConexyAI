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
}
