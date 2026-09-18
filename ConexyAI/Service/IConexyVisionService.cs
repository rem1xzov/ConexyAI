namespace ConexyAI.Service;

public interface IConexyVisionService
{
    Task<string> CaptureScreenshotBase64Async(
        string targetUrlOrPath,
        int viewportWidth = 1280,
        int viewportHeight = 800,
        CancellationToken ct = default);
}