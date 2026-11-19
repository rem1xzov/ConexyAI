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

    // LOCAL_WORKER: добавлено 2026-11-19 — пользовательская локальная модель как субагент. Wire —
    // OpenAI-совместимый chat/completions, поэтому подходят Ollama, LM Studio и llama.cpp server.
    public LocalModelOptions Local { get; set; } = new();
}

public class LocalModelOptions
{
    /// <summary>Enables the local route; stays off until a real endpoint is configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible base URL (Ollama default: http://127.0.0.1:11434/v1).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1";

    /// <summary>Optional key; local servers usually ignore it.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model name of the local server.</summary>
    public string Model { get; set; } = "qwen2.5-coder:7b";

    /// <summary>Whether the local model can emit tool calls (most coder models can).</summary>
    public bool SupportsToolCalls { get; set; } = true;

    /// <summary>Output token cap of the local model.</summary>
    public int MaxTokens { get; set; } = 4096;
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
