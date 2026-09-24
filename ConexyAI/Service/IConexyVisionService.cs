namespace ConexyAI.Service;

public interface IConexyVisionService
{
    /// <summary>
    /// Whether headless Chromium can actually be launched in this environment.
    /// <para>
    /// AGENT_TOOL_FAILURES: added 2026-09-23. The agent tool list advertises <c>take_screenshot</c>,
    /// but the browser is a runtime dependency that may be missing (it is installed on first use and
    /// the production image does not necessarily carry it). Without this probe the model kept calling
    /// a tool that could never succeed, and every call ended the whole run with an exception.
    /// </para>
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Renders a page and returns it as a base64 JPEG.
    /// <para>
    /// H9: changed 2026-09-24. <paramref name="targetUrlOrPath"/> is either a PUBLIC http(s) URL or a
    /// file of the chat workspace identified by <paramref name="chatId"/> (relative path,
    /// <c>/workspace/…</c> as the sandbox shows it, or <c>file://</c> inside the workspace). Anything
    /// else — host files, other chats, internal hosts, cloud metadata — is refused with
    /// <see cref="Web.ScreenshotTargetException"/>, and so is every sub-resource or redirect of the page
    /// that leaves those bounds.
    /// </para>
    /// </summary>
    Task<string> CaptureScreenshotBase64Async(
        Guid chatId,
        string targetUrlOrPath,
        int viewportWidth = 1280,
        int viewportHeight = 800,
        CancellationToken ct = default);
}
