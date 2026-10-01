using System.Runtime.CompilerServices;
using ConexyAI.Model;
using ConexyAI.Service;

// ORCHESTRA: добавлено 2026-09-28
/// <summary>
/// Доступ к «Оркестру агентов»: только тариф ProMax и только режим Coder. Проверка идёт на сервере,
/// поэтому её правила должны быть зафиксированы тестом, а не только текстом в интерфейсе.
/// </summary>
internal static class OrchestraTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("orchestra: ProMax needs the coder mode, and only ProMax (or an admin)", EligibilityAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task EligibilityAsync()
    {
        // ProMax и админ — можно (админ считается как ProMax: у него полный доступ).
        Assert(OrchestraEligibility.IsAllowed("ProMax", false, ConexyModelType.ConexyCoder),
            "ProMax must be allowed in the coder mode");
        Assert(OrchestraEligibility.IsAllowed("Admin", true, ConexyModelType.ConexyCoder),
            "an admin must be allowed");
        Assert(OrchestraEligibility.IsAllowed("ProMax", true, ConexyModelType.ConexyCoder),
            "an admin on ProMax must be allowed");
        // ANNUAL_ULTRA: годовой тариф выше ProMax — оркестр ему тоже доступен.
        Assert(OrchestraEligibility.IsAllowed("Ultra", false, ConexyModelType.ConexyCoder),
            "Ultra must be allowed in the coder mode");

        // Младшие тарифы — нельзя.
        foreach (var tier in new[] { "Free", "Go", "Pro", "" })
        {
            Assert(!OrchestraEligibility.IsAllowed(tier, false, ConexyModelType.ConexyCoder),
                $"tier '{tier}' must not be allowed");
        }
        Assert(!OrchestraEligibility.IsAllowed(null, false, ConexyModelType.ConexyCoder),
            "an unknown tier must not be allowed");

        // Cowork: всегда один агент, даже на ProMax.
        Assert(!OrchestraEligibility.IsAllowed("ProMax", false, ConexyModelType.ConexyCowork),
            "Cowork must never use the orchestra");
        Assert(!OrchestraEligibility.IsAllowed("ProMax", true, ConexyModelType.ConexyCowork),
            "even an admin must not use the orchestra in Cowork");
        Assert(!OrchestraEligibility.IsAllowed("ProMax", true, ConexyModelType.ConexyV1Flash),
            "the chat modes must never use the orchestra");

        return Task.CompletedTask;
    }
}
