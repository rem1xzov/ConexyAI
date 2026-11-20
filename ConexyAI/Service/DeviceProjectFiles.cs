namespace ConexyAI.Service;

// DEVICE_PROJECT_FILES: добавлено 2026-11-20 — правила проекта и хуки раньше читались с ПК ДВУМЯ
// device-вызовами project_rules подряд (rules, потом hooks.json). Один вызов на все файлы дешевле по
// задержке, поэтому чтение объединено, а разбор вынесен в чистую функцию, которую легко проверить.
internal static class DeviceProjectFiles
{
    /// <summary>Разделяет ответ клиента на файлы правил и содержимое хуков.</summary>
    public static (List<(string File, string Content)> Rules, string? Hooks) Split(
        IReadOnlyList<(string File, string Content)> files, string hooksFile, IReadOnlyList<string> ruleFiles)
    {
        var rules = files
            .Where(file => ruleFiles.Any(rule => rule.Equals(file.File, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var hooks = files
            .FirstOrDefault(file => file.File.Equals(hooksFile, StringComparison.OrdinalIgnoreCase))
            .Content;
        return (rules, hooks);
    }
}
