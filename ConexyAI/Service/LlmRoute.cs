using ConexyAI.Configuration;
using ConexyAI.Model;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// LOCAL_WORKER: добавлено 2026-11-19 — маршрут модели как отдельная сущность. Облачная модель остаётся
// ведущей, а локальная (Ollama / LM Studio / llama.cpp server, OpenAI-совместимый wire) может быть
// субагентом. Раньше маршрут был зашит в switch по ConexyModelType, и выбрать модель для воркера было нельзя.
public sealed record LlmRoute(
    string ProviderId,
    string BaseUrl,
    string ApiKey,
    string Model,
    bool Local,
    bool SupportsToolCalls,
    int MaxTokens);

public interface ILlmRouteResolver
{
    /// <summary>Whether a local OpenAI-compatible endpoint is configured for this deployment.</summary>
    bool LocalAvailable { get; }

    /// <summary>The local route, or <c>null</c> when it is not configured.</summary>
    LlmRoute? Local { get; }

    /// <summary>The cloud route of the given public model (vision turns fall back to the vision model).</summary>
    LlmRoute Cloud(ConexyModelType modelType, bool vision);

    /// <summary>The requested route, or the cloud route when none was requested.</summary>
    LlmRoute Resolve(ConexyModelType modelType, bool vision, LlmRoute? requested);
}

public sealed class LlmRouteResolver : ILlmRouteResolver
{
    private const int CloudMaxTokens = 8192;

    private readonly AiUpstreamOptions _options;

    public LlmRouteResolver(IOptions<AiUpstreamOptions> options)
    {
        _options = options.Value;
    }

    public bool LocalAvailable =>
        _options.Local is { Enabled: true } local
        && !string.IsNullOrWhiteSpace(local.BaseUrl)
        && !string.IsNullOrWhiteSpace(local.Model);

    public LlmRoute? Local
    {
        get
        {
            if (!LocalAvailable)
                return null;
            var local = _options.Local;
            return new LlmRoute(
                "local",
                local.BaseUrl.TrimEnd('/'),
                local.ApiKey ?? string.Empty,
                local.Model,
                Local: true,
                local.SupportsToolCalls,
                local.MaxTokens > 0 ? local.MaxTokens : 4096);
        }
    }

    public LlmRoute Cloud(ConexyModelType modelType, bool vision)
    {
        var deepSeek = _options.DeepSeek;
        if (string.IsNullOrWhiteSpace(deepSeek.ApiKey))
            throw new InvalidOperationException("DeepSeek API key is not configured in appsettings.json");

        // PRO_VISION: у deepseek-v4-pro нет зрения — ход с картинками идёт на flash-модель.
        var model = modelType switch
        {
            ConexyModelType.ConexyV1Flash => deepSeek.FlashModel,
            ConexyModelType.ConexyV1Pro => vision ? deepSeek.FlashModel : deepSeek.ProAgentModel,
            // COWORK_MODE: оба агентских режима работают на одной upstream-модели.
            ConexyModelType.ConexyCoder or ConexyModelType.ConexyCowork => deepSeek.FlashModel,
            _ => throw new ArgumentOutOfRangeException(nameof(modelType), modelType, "Unsupported model type."),
        };

        return new LlmRoute("deepseek", deepSeek.BaseUrl.TrimEnd('/'), deepSeek.ApiKey, model, Local: false, SupportsToolCalls: true, CloudMaxTokens);
    }

    public LlmRoute Resolve(ConexyModelType modelType, bool vision, LlmRoute? requested) =>
        requested ?? Cloud(modelType, vision);
}
