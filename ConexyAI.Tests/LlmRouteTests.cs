using System.Runtime.CompilerServices;
using ConexyAI.Configuration;
using ConexyAI.Model;
using ConexyAI.Service;
using Microsoft.Extensions.Options;

// LOCAL_WORKER: добавлено 2026-11-19
/// <summary>
/// Маршруты моделей: облачный по умолчанию, локальный (OpenAI-совместимый) — только когда настроен.
/// Локальный маршрут — для субагентов, ведущая модель остаётся облачной.
/// </summary>
internal static class LlmRouteTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("llm routes: the cloud route follows the public model and the vision fallback", CloudAsync);
        TestRegistry.Add("llm routes: the local route exists only when configured", LocalAsync);
        TestRegistry.Add("llm routes: a requested local route wins over the cloud route", RequestedAsync);
        TestRegistry.Add("llm routes: a missing cloud key fails loudly", MissingKeyAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static LlmRouteResolver NewResolver(bool localEnabled = false, string deepSeekKey = "sk-test") =>
        new(Options.Create(new AiUpstreamOptions
        {
            DeepSeek = new DeepSeekOptions
            {
                BaseUrl = "https://api.deepseek.com/v1/",
                ApiKey = deepSeekKey,
                FlashModel = "deepseek-flash",
                ProAgentModel = "deepseek-v4-pro",
            },
            Local = new LocalModelOptions
            {
                Enabled = localEnabled,
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Model = "qwen2.5-coder:7b",
                MaxTokens = 3072,
            },
        }));

    private static Task CloudAsync()
    {
        var resolver = NewResolver();

        var coder = resolver.Cloud(ConexyModelType.ConexyCoder, vision: false);
        Assert(!coder.Local && coder.ProviderId == "deepseek", "the coder runs on the cloud route");
        Assert(coder.Model == "deepseek-flash", $"the coder uses the flash model, got {coder.Model}");
        Assert(coder.MaxTokens == 8192, "the cloud route keeps the 8192 output cap");

        var pro = resolver.Cloud(ConexyModelType.ConexyV1Pro, vision: false);
        Assert(pro.Model == "deepseek-v4-pro", "Pro uses the Pro model");

        var proVision = resolver.Cloud(ConexyModelType.ConexyV1Pro, vision: true);
        Assert(proVision.Model == "deepseek-flash", "a Pro turn with images falls back to the vision model");

        Assert(resolver.Cloud(ConexyModelType.ConexyV1Flash, false).BaseUrl == "https://api.deepseek.com/v1",
            "the base URL is normalized without a trailing slash");
        return Task.CompletedTask;
    }

    private static Task LocalAsync()
    {
        var disabled = NewResolver(localEnabled: false);
        Assert(!disabled.LocalAvailable && disabled.Local is null, "an unconfigured local route must not exist");

        var enabled = NewResolver(localEnabled: true);
        Assert(enabled.LocalAvailable, "an enabled local route must be available");
        var route = enabled.Local!;
        Assert(route.Local && route.ProviderId == "local", "the local route is marked local");
        Assert(route.BaseUrl == "http://127.0.0.1:11434/v1", $"the local base URL is normalized, got {route.BaseUrl}");
        Assert(route.Model == "qwen2.5-coder:7b" && route.MaxTokens == 3072, "the local model and cap come from config");
        return Task.CompletedTask;
    }

    private static Task RequestedAsync()
    {
        var resolver = NewResolver(localEnabled: true);
        var local = resolver.Local!;
        var resolved = resolver.Resolve(ConexyModelType.ConexyCoder, vision: false, local);
        Assert(resolved.Local && resolved.ProviderId == "local", "a requested route must win");

        var cloud = resolver.Resolve(ConexyModelType.ConexyCoder, vision: false, null);
        Assert(!cloud.Local, "without a requested route the cloud route is used");
        return Task.CompletedTask;
    }

    private static Task MissingKeyAsync()
    {
        var resolver = NewResolver(deepSeekKey: "");
        try
        {
            resolver.Cloud(ConexyModelType.ConexyCoder, vision: false);
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message.Contains("API key", StringComparison.Ordinal), "the failure must name the missing key: " + ex.Message);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("Assertion failed: a missing cloud key must fail loudly");
    }
}
