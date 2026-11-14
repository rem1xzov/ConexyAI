namespace ConexyAI.Desktop.Tools;

// WRITE_CHURN: в реальном прогоне агент 15 раз подряд полностью перезаписывал один и тот же файл, а проверка
// возвращала тот же вывод. Это не петля «один в один» (аргументы менялись), но тактика не менялась. Признак
// универсален: полная перезапись — дорогой и разрушительный способ правки, после нескольких заходов она
// означает, что причина не понята.
/// <summary>Предел на полные перезаписи одного файла за сеанс клиента.</summary>
public sealed class WriteChurnGuard
{
    /// <summary>Сколько полных перезаписей одного файла допустимо, прежде чем сменить тактику.</summary>
    public const int RewriteLimit = 6;

    private readonly Dictionary<string, int> _writes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Новый ход/задача: счётчик начинается с нуля.</summary>
    public void Reset() => _writes.Clear();

    /// <summary>Почему вызов надо отклонить (null — можно выполнять).</summary>
    public string? Refuse(string tool, ToolArgs args)
    {
        var path = Target(tool, args);
        if (path is null)
            return null;
        var count = _writes.GetValueOrDefault(path);
        if (count < RewriteLimit)
            return null;
        return $"Файл '{path}' уже полностью перезаписан {count} раз за этот сеанс, и тактика не менялась. " +
               "Сначала прочитай актуальный вывод проверки целиком и назови причину; правь точечно инструментом " +
               "edit (или apply_patch) по этому фрагменту, а не перезаписывай файл заново. " +
               "Если правка не нужна — остановись и скажи, что именно не сходится.";
    }

    /// <summary>Учитывает реально выполненную полную перезапись.</summary>
    public void Record(string tool, ToolArgs args)
    {
        if (Target(tool, args) is { } path)
            _writes[path] = _writes.GetValueOrDefault(path) + 1;
    }

    /// <summary>Только полная запись файла: точечные правки (edit/apply_patch) не считаются перезаписью.</summary>
    private static string? Target(string tool, ToolArgs args)
    {
        if (tool is not ("file_write" or "file_write_bytes"))
            return null;
        var path = args.Str("path") ?? args.Str("file_path");
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim().Replace('\\', '/');
    }
}
