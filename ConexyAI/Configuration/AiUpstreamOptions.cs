namespace ConexyAI.Configuration;

/// <summary>
/// Binds the <c>AiUpstream</c> section of appsettings.json. This is the single
/// source of truth for mapping public Conexy model names to their upstream
/// provider. Upstream names never leave this layer.
/// </summary>
public class AiUpstreamOptions
{
    public const string SectionName = "AiUpstream";

    public DeepSeekOptions DeepSeek { get; set; } = new();
}

public class DeepSeekOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Fast chat model used by the dialog stream (ConexyV1-flash).</summary>
    public string FlashModel { get; set; } = "deepseek-flash";

    /// <summary>Model shared by ConexyV1-pro and the conexy-coder agent pipeline.</summary>
    public string ProAgentModel { get; set; } = "deepseek-v4-pro";
}
