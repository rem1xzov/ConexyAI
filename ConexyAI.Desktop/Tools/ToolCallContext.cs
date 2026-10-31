namespace ConexyAI.Desktop.Tools;

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>
/// Контекст текущего вызова инструмента (задача и откуда она пришла). Живёт в AsyncLocal: интерфейс
/// <see cref="ICommandApprover"/> и PathGuard про вызов ничего не знают, а решение «спросить в консоли или
/// на сайте» и «к какой задаче отнести правку» принимается по нему.
/// </summary>
public sealed record ToolCallInfo(Guid TaskId, bool Remote, string ProjectRoot)
{
    /// <summary>REMOTE_APPROVAL_POLICY: имя выполняемого инструмента (ставит ToolRunner).</summary>
    public string? Tool { get; init; }
}

public static class ToolCallContext
{
    private static readonly AsyncLocal<ToolCallInfo?> CurrentValue = new();

    public static ToolCallInfo? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }
}
