namespace ConexyAI.Service.Prompts;

// INIT_COMMAND: добавлено 2026-11-09 — «/init» как в Claude Code: агент изучает проект и создаёт
// CONEXY.md, который затем автоматически попадает в каждый запрос (см. ProjectRuleFiles).
/// <summary>The canned task behind the <c>/init</c> command, shared by the web and the desktop client.</summary>
public static class BuiltInCommands
{
    /// <summary>True when the prompt is the <c>/init</c> command (and not e.g. <c>/initialize</c>).</summary>
    public static bool IsInit(string? prompt)
    {
        var text = prompt?.TrimStart() ?? string.Empty;
        if (!text.StartsWith("/init", StringComparison.OrdinalIgnoreCase))
            return false;
        return text.Length == 5 || char.IsWhiteSpace(text[5]);
    }

    /// <summary>Expands <c>/init [focus]</c> into the agent's task; other prompts pass through unchanged.</summary>
    public static string Expand(string prompt, string? focus = null)
    {
        if (!IsInit(prompt))
            return prompt;
        var arg = (focus ?? prompt.TrimStart()[5..]).Trim();
        return Init(arg);
    }

    /// <summary>The instruction that makes the agent write a project guide the next runs will follow.</summary>
    public static string Init(string? focus)
    {
        var subject = string.IsNullOrWhiteSpace(focus) ? "весь проект" : focus.Trim();
        return
            "Проведи /init: изучи этот проект и создай (или дополни) файл CONEXY.md в корне рабочей области. " +
            "Этот файл автоматически читается в каждом следующем запуске, поэтому он должен быть точным и коротким.\n\n" +
            "Что сделать:\n" +
            "1. Осмотри проект: дерево каталогов (пропуская node_modules, .git, bin, obj, dist, build, vendor), README, " +
            "манифесты (package.json, *.csproj, pyproject.toml, go.mod, Cargo.toml, composer.json), конфиги сборки, " +
            "тестов и линтера, CI, Dockerfile, существующие CONEXY.md, CLAUDE.md, AGENTS.md и .conexy/rules.md.\n" +
            "2. Определи: назначение проекта, стек и ключевые зависимости, архитектуру и роль основных каталогов, " +
            "как собрать/запустить/протестировать/проверить типы, соглашения кода (стиль, именование, структура), " +
            "важные особенности и подводные камни.\n" +
            "3. Создай CONEXY.md в корне. Если файл уже есть — дополни его и сохрани существующие разделы. " +
            "Разделы: Обзор, Стек, Структура, Команды, Соглашения, Важные детали, Чего не делать.\n" +
            "4. В «Командах» указывай только реально существующие команды из конфигов проекта — не выдумывай их. " +
            "Если команда неизвестна, проверь конфиг или не пиши её.\n" +
            "5. Пиши фактами из репозитория, без общих фраз и без воды. Код приложения не меняй.\n\n" +
            $"Фокус: {subject}.\n" +
            "В конце ответа коротко перечисли, что записано в CONEXY.md.";
    }
}
