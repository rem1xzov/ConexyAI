using System.Runtime.CompilerServices;
using ConexyAI.Model;
using ConexyAI.Service;

// FLASH_TIMEOUT: добавлено 2026-09-27; изменено 2026-10-01
/// <summary>
/// FLASH_TIMEOUT: порог «нет прогресса» у Flash поднят с 20 до 60 секунд — с большим вложением
/// модель честно думает дольше 20 секунд, и прежний сторож обрывал живой запрос. Тест фиксирует
/// новое значение и то, что сообщение об обрыве называет модель, время и просит обновить страницу.
/// Логика чистая (без сети), поэтому проверяется напрямую.
/// </summary>
internal static class FlashTimeoutTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("flash: a silent stream is cut after 60 seconds", FlashTimeoutAsync);
        TestRegistry.Add("flash: other chat models keep the longer stall guard", OtherModelsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task FlashTimeoutAsync()
    {
        Assert(
            ConexyLlmClient.NoProgressTimeoutFor(ConexyModelType.ConexyV1Flash) == TimeSpan.FromSeconds(60),
            "Flash must abort a stream after 60 seconds without progress");

        var message = ConexyLlmClient.TimeoutMessageFor(ConexyModelType.ConexyV1Flash);
        Assert(message.Contains("Flash"), $"the message must name the model, got: {message}");
        Assert(message.Contains("60"), $"the message must state the timeout, got: {message}");
        Assert(message.Contains("Обновите страницу"), $"the message must tell the user to reload, got: {message}");

        return Task.CompletedTask;
    }

    private static Task OtherModelsAsync()
    {
        Assert(
            ConexyLlmClient.NoProgressTimeoutFor(ConexyModelType.ConexyV1Pro) == TimeSpan.FromSeconds(60),
            "Pro keeps the 60-second stall guard");
        Assert(
            ConexyLlmClient.NoProgressTimeoutFor(ConexyModelType.ConexyCoder) == TimeSpan.FromSeconds(60),
            "the agent keeps the 60-second stall guard");

        var message = ConexyLlmClient.TimeoutMessageFor(ConexyModelType.ConexyV1Pro);
        Assert(message.Contains("Обновите страницу"), $"the message must tell the user to reload, got: {message}");

        return Task.CompletedTask;
    }
}
