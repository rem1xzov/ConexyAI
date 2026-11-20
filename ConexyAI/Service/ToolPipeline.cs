namespace ConexyAI.Service;

// TOOL_PIPELINE: добавлено 2026-11-20 (идея DeepSeek Harness, packages/core/tools pipeline) — отказные
// гейты инструмента вынесены в упорядоченный конвейер. Свойство, которое важно для безопасности:
// конвейер МОНОТОННЫЙ — гейт умеет только отказать, первый отказ побеждает, и следующий гейт не может
// отменить предыдущий. Раньше проверки были лестницей тернарников в раннере: добавить новую значило
// править большую цепочку и надеяться, что условия не перепутаны.
public interface IToolGate
{
    /// <summary>Имя гейта для логов и тестов.</summary>
    string Name { get; }

    /// <summary>Причина отказа или <c>null</c>, если вызов можно выполнять.</summary>
    string? Refuse(string tool, string arguments);
}

/// <summary>Последовательность отказных гейтов: первый непустой отказ останавливает проверку.</summary>
public sealed class ToolGatePipeline
{
    private readonly IReadOnlyList<IToolGate> _gates;

    public ToolGatePipeline(params IToolGate[] gates) => _gates = gates;

    public IReadOnlyList<string> Names => _gates.Select(gate => gate.Name).ToList();

    /// <summary>Отказ первого сработавшего гейта; <c>null</c> — все пропустили.</summary>
    public string? Refuse(string tool, string arguments)
    {
        foreach (var gate in _gates)
        {
            var refusal = gate.Refuse(tool, arguments);
            if (!string.IsNullOrWhiteSpace(refusal))
                return refusal;
        }

        return null;
    }
}

/// <summary>Гейт стража цикла: одинаковые вызовы и выводы, повторные чтения и маршрутные отказы.</summary>
public sealed class LoopGuardGate(AgentLoopGuard guard) : IToolGate
{
    public string Name => "loop_guard";

    public string? Refuse(string tool, string arguments) => guard.Refuse(tool, arguments);
}

/// <summary>Гейт computer use: клик в пустоту, застывший кадр, повтор одной цели.</summary>
public sealed class ComputerUseGate(ComputerUseGuard guard) : IToolGate
{
    public string Name => "computer_use";

    public string? Refuse(string tool, string arguments) => guard.Refuse(tool, arguments);
}
