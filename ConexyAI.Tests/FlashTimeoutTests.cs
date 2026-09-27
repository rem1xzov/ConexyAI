using System.Runtime.CompilerServices;
using ConexyAI.Model;
using ConexyAI.Service;

// FLASH_TIMEOUT: добавлено 2026-09-27
/// <summary>
/// Flash — быстрая модель, и «думать» минуту она не должна. Проверяем, что у неё свой короткий
/// порог «нет прогресса» (20 секунд) и понятное пользователю сообщение об обрыве. Логика чистая
/// (без сети), поэтому проверяется напрямую.
/// </summary>
internal static class FlashTimeoutTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("flash: a silent stream is cut after 20 seconds", FlashTimeoutAsync);
        TestRegistry.Add("flash: other chat models keep the longer stall guard", OtherModelsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task FlashTimeoutAsync()
    {
        Assert(
            ConexyLlmClient.NoProgressTimeoutFor(ConexyModelType.ConexyV1Flash) == TimeSpan.FromSeconds(20),
            "Flash must abort a stream after 20 seconds without progress");

        var message = ConexyLlmClient.TimeoutMessageFor(ConexyModelType.ConexyV1Flash);
        Assert(message.Contains("Flash"), $"the message must name the model, got: {message}");
        Assert(message.Contains("20"), $"the message must state the timeout, got: {message}");
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
