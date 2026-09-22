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

    Task<string> CaptureScreenshotBase64Async(
        string targetUrlOrPath,
        int viewportWidth = 1280,
        int viewportHeight = 800,
        CancellationToken ct = default);
}
