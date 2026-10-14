namespace ConexyAI.Configuration;

/// <summary>Binds the <c>Preview</c> section: long-running agent services exposed through the preview gateway.</summary>
public class PreviewOptions
{
    public const string SectionName = "Preview";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Wildcard base domain: a service is served at <c>{id}.{Domain}</c>. DNS <c>*.{Domain}</c> and the
    /// edge proxy must route it to the backend (see frontend/nginx.conf).
    /// </summary>
    public string Domain { get; set; } = "preview.conexyai.ru";

    /// <summary>URL scheme of the public preview address.</summary>
    public string Scheme { get; set; } = "https";

    /// <summary>
    /// Docker network that service containers join. The backend must be attached to it too (it resolves
    /// containers by name). Create once: <c>docker network create conexy-preview-net</c>.
    /// </summary>
    public string Network { get; set; } = "conexy-preview-net";

    /// <summary>Service is stopped after this many minutes without a start/touch.</summary>
    public int MaxLifetimeMinutes { get; set; } = 60;

    /// <summary>How long start_service waits for the port to start answering.</summary>
    public int StartupWaitSeconds { get; set; } = 25;
}
