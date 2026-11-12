using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ConexyAI.Contract;
using ConexyAI.Service;

// MODEL_FACING_TOOLS: каталог схем — это то, что модель читает на каждом шаге, поэтому его
// инварианты проверяются тестами, а не глазами. Ошибка в схеме здесь стоит не «некрасивого
// описания», а неверного выбора инструмента и лишних шагов агента.
internal static class AgentToolCatalogTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("tools: catalog invariants hold", InvariantsAsync);
        TestRegistry.Add("tools: every required parameter is enforced", RequiredParametersAsync);
        TestRegistry.Add("tools: wrong types and enums are rejected", TypesAndEnumsAsync);
        TestRegistry.Add("tools: minimal valid arguments pass for every tool", MinimalArgumentsAsync);
        TestRegistry.Add("tools: foreign tools are not validated by this catalog", ForeignToolsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task InvariantsAsync()
    {
        var problems = AgentToolCatalog.Validate();
        Assert(problems.Count == 0, "catalog invariants: " + string.Join(" | ", problems));

        var names = AgentToolCatalog.Names();
        Assert(names.Count >= 35, $"the catalog advertises the agent tool set, got {names.Count}");

        // Инструменты, без которых агент не решает базовые задачи, обязаны быть в наборе.
        foreach (var required in new[]
                 {
                     "read", "write", "edit", "apply_patch", "grep", "glob",
                     "bash", "run_tests", "get_diagnostics", "todo_write", "web_search", "fetch_web_page",
                     "github_action", "github_api", "create_document", "read_document_file", "read_image",
                 })
        {
            Assert(names.Contains(required), $"the catalog must offer {required}");
        }

        // Каждое имя — ровно один раз: синоним одного действия и есть источник неверного выбора.
        Assert(names.Distinct(StringComparer.Ordinal).Count() == names.Count, "tool names must be unique");
        return Task.CompletedTask;
    }

    private static Task RequiredParametersAsync()
    {
        var missingPath = Refusal("read", "{}");
        Assert(missingPath is not null && missingPath.Contains("missing required parameter 'file_path'", StringComparison.Ordinal),
            "read without file_path must be refused with the parameter name: " + missingPath);

        var missingCommand = Refusal("edit", """{"file_path":"a.cs"}""");
        Assert(missingCommand is not null && missingCommand.Contains("'old_string'", StringComparison.Ordinal),
            "edit without old_string must name the missing parameter: " + missingCommand);

        var missingDescription = Refusal("bash", """{"command":"dotnet build"}""");
        Assert(missingDescription is not null && missingDescription.Contains("'description'", StringComparison.Ordinal),
            "bash without description must be refused: " + missingDescription);

        var nullPath = Refusal("read", """{"file_path":null}""");
        Assert(nullPath is not null, "an explicit null is the same as a missing parameter");

        // Отказ обязан показывать ожидаемую форму: иначе модель повторяет тот же вызов.
        Assert(missingPath!.Contains("Expected:", StringComparison.Ordinal), "the refusal lists the expected parameters");
        Assert(missingPath.Contains("file_path (required): string", StringComparison.Ordinal),
            "the expected shape marks required parameters: " + missingPath);
        return Task.CompletedTask;
    }

    private static Task TypesAndEnumsAsync()
    {
        var wrongType = Refusal("read", """{"file_path":123}""");
        Assert(wrongType is not null && wrongType.Contains("'file_path' must be string", StringComparison.Ordinal),
            "a numeric path must be refused: " + wrongType);

        var wrongInteger = Refusal("grep", """{"pattern":"x","max_results":"10"}""");
        Assert(wrongInteger is not null && wrongInteger.Contains("'max_results' must be integer", StringComparison.Ordinal),
            "a stringified limit must be refused: " + wrongInteger);

        var wrongEnum = Refusal("grep", """{"pattern":"x","output_mode":"everything"}""");
        Assert(wrongEnum is not null && wrongEnum.Contains("must be one of", StringComparison.Ordinal),
            "an unknown editor command must list the allowed values: " + wrongEnum);

        var goodEnum = Refusal("grep", """{"pattern":"x","output_mode":"files"}""");
        Assert(goodEnum is null, "a valid enum value passes: " + goodEnum);

        // HARNESS_ALIASES: имена harness переводятся на внутренние, аргументы — под старый контракт.
        static LlmToolCall Call(string name, string arguments) =>
            new("call-1", "function", new LlmFunctionCall(name, arguments));
        var read = AgentToolCatalog.Normalize(Call("read", """{"file_path":"a.ts","offset":5,"limit":10}"""));
        Assert(read.Function.Name == "file_read" && read.Function.Arguments.Contains("\"offset\":4") &&
               read.Function.Arguments.Contains("\"path\":\"a.ts\""), "read maps to file_read with a 0-based offset: " + read.Function.Arguments);
        var write = AgentToolCatalog.Normalize(Call("write", """{"file_path":"a.ts","content":"x"}"""));
        Assert(write.Function.Name == "file_write" && write.Function.Arguments.Contains("\"content\":\"x\""), "write maps to file_write");
        var edit = AgentToolCatalog.Normalize(Call("edit", """{"file_path":"a.ts","old_string":"a","new_string":"b"}"""));
        Assert(edit.Function.Name == "str_replace_editor" && edit.Function.Arguments.Contains("\"command\":\"str_replace\"") &&
               edit.Function.Arguments.Contains("\"old_str\":\"a\""), "edit maps to str_replace_editor: " + edit.Function.Arguments);
        var legacy = AgentToolCatalog.Normalize(Call("file_read", """{"path":"a.ts"}"""));
        Assert(legacy.Function.Name == "file_read" && legacy.Function.Arguments.Contains("\"path\":\"a.ts\""), "old names pass through unchanged");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every advertised tool accepts its own minimal argument set. A schema that requires a parameter
    /// its dispatcher never reads (or the reverse) fails here instead of in production.
    /// </summary>
    private static Task MinimalArgumentsAsync()
    {
        foreach (var name in AgentToolCatalog.Names())
        {
            var schema = AgentToolCatalog.SchemaFor(name)!.Value;
            var parameters = schema.GetProperty("function").GetProperty("parameters");
            var json = new StringBuilder("{");
            var first = true;
            foreach (var requiredName in parameters.GetProperty("required").EnumerateArray())
            {
                var parameter = requiredName.GetString()!;
                var declaration = parameters.GetProperty("properties").GetProperty(parameter);
                var type = declaration.GetProperty("type").GetString();
                // Enum-параметр принимает только своё значение: подставляем первое из списка.
                var sample = declaration.TryGetProperty("enum", out var allowed)
                    ? "\"" + allowed.EnumerateArray().First().GetString() + "\""
                    : SampleValueFor(type);
                if (!first) json.Append(',');
                first = false;
                json.Append('"').Append(parameter).Append("\":").Append(sample);
            }
            json.Append('}');

            var refusal = Refusal(name, json.ToString());
            Assert(refusal is null, $"minimal valid arguments for {name} must pass: {refusal}");
        }
        return Task.CompletedTask;
    }

    private static string SampleValueFor(string? type) => type switch
    {
        "integer" or "number" => "1",
        "boolean" => "true",
        "array" => "[]",
        "object" => "{}",
        _ => "\"x\"",
    };

    private static Task ForeignToolsAsync()
    {
        // MCP, инструменты ПК и телефона приходят со своими схемами — этот каталог их не проверяет.
        Assert(Refusal("mcp__notion__search_pages", "{}") is null, "MCP tools are validated by their server");
        Assert(Refusal("pc_read_file", "{}") is null, "device tools are validated by the client");
        Assert(Refusal("no_such_tool", "{}") is null, "an unknown name is left to the dispatcher");
        return Task.CompletedTask;
    }

    private static string? Refusal(string tool, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return ConexyAgentRunner.ToolArgumentError(tool, document.RootElement);
    }
}
