using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ConexyAI.Contract;
using ConexyAI.Service;

// LSP_LITE: добавлено 2026-10-05 — тесты лёгкого парсера символов (Outline / go-to-definition / hover).
internal static class SymbolTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("lsp outline: a C# file nests namespace → class → methods/fields", CSharpOutlineAsync);
        TestRegistry.Add("lsp outline: a TypeScript file finds interface, class, method and arrow function", TypeScriptOutlineAsync);
        TestRegistry.Add("lsp outline: a Python file nests class → defs and ignores imports", PythonOutlineAsync);
        TestRegistry.Add("lsp definition: a call is not a declaration, the declaration is", DefinitionSkipsCallsAsync);
        TestRegistry.Add("lsp definition: the grep pattern matches only real declarations", GrepPatternMatchesDeclarationsAsync);
        TestRegistry.Add("lsp hover: the comment block above a declaration is the documentation", DocumentationAsync);
    }

    private static Task CSharpOutlineAsync()
    {
        const string code = """
            using System;

            namespace Demo
            {
                /// <summary>Counts things.</summary>
                public class Counter
                {
                    private int _value;

                    public void Increment(int by)
                    {
                    }

                    public string Describe(int x)
                    {
                        return x.ToString();
                    }
                }
            }
            """;

        var symbols = SourceSymbolParser.Parse("Counter.cs", code);
        TestRegistry.Assert(symbols.Count == 1, "one top-level symbol (the namespace), got " + symbols.Count);
        TestRegistry.Assert(symbols[0].Name == "Demo" && symbols[0].Kind == "module", "namespace Demo parsed as module");

        var counter = symbols[0].Children.Single(c => c.Name == "Counter");
        TestRegistry.Assert(counter.Kind == "class", "Counter is a class");

        var increment = counter.Children.Single(c => c.Name == "Increment");
        TestRegistry.Assert(increment.Kind == "method", "Increment is a method");
        TestRegistry.Assert(counter.Children.Any(c => c.Name == "_value" && c.Kind == "field"), "the field _value is listed");
        TestRegistry.Assert(counter.EndLine >= counter.Children.Single(c => c.Name == "Describe").Line, "the class range covers its methods");
        return Task.CompletedTask;
    }

    private static Task TypeScriptOutlineAsync()
    {
        const string code = """
            import { x } from './x';

            export interface Point {
              x: number;
            }

            export class Shape {
              area(): number {
                return 0;
              }
            }

            export const make = (n: number): Shape => new Shape();
            """;

        var symbols = SourceSymbolParser.Parse("shape.ts", code);
        var point = symbols.Single(s => s.Name == "Point");
        var shape = symbols.Single(s => s.Name == "Shape");
        var make = symbols.Single(s => s.Name == "make");

        TestRegistry.Assert(point.Kind == "interface", "Point is an interface");
        TestRegistry.Assert(shape.Kind == "class", "Shape is a class");
        TestRegistry.Assert(shape.Children.Any(c => c.Name == "area" && c.Kind == "method"), "area is a method of Shape");
        TestRegistry.Assert(make.Kind == "function", "an arrow assignment is a function");
        return Task.CompletedTask;
    }

    private static Task PythonOutlineAsync()
    {
        const string code = "import os\n\n" +
                            "class Animal:\n" +
                            "    def __init__(self, name):\n" +
                            "        self.name = name\n\n" +
                            "    def speak(self):\n" +
                            "        return \"...\"\n";

        var symbols = SourceSymbolParser.Parse("animal.py", code);
        TestRegistry.Assert(symbols.Count == 1 && symbols[0].Name == "Animal", "only the class is top-level");
        TestRegistry.Assert(symbols[0].Children.Any(c => c.Name == "__init__" && c.Kind == "function"), "__init__ is nested");
        TestRegistry.Assert(symbols[0].Children.Any(c => c.Name == "speak"), "speak is nested");
        TestRegistry.Assert(symbols.All(s => s.Name != "os"), "the import directive is not a symbol");
        return Task.CompletedTask;
    }

    private static Task DefinitionSkipsCallsAsync()
    {
        const string code = "foo();\nfunction foo() {}\n";
        var found = SourceSymbolParser.FindInContent("a.ts", code, "foo").ToList();
        TestRegistry.Assert(found.Count == 1, "only the declaration is found, got " + found.Count);
        TestRegistry.Assert(found[0].Location.Line == 2 && found[0].Location.Kind == "function", "the function declaration on line 2");
        return Task.CompletedTask;
    }

    private static Task GrepPatternMatchesDeclarationsAsync()
    {
        var pattern = SourceSymbolParser.BuildGrepPattern("Increment");
        var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));

        TestRegistry.Assert(regex.IsMatch("        public void Increment(int by)"), "a typed method declaration matches");
        TestRegistry.Assert(regex.IsMatch("    public Increment();"), "an untyped constructor matches");
        TestRegistry.Assert(regex.IsMatch("export function Increment() {}"), "a keyword function matches");
        TestRegistry.Assert(!regex.IsMatch("        Increment(3);"), "a bare call does not match");
        TestRegistry.Assert(!regex.IsMatch("        counter.Increment(3);"), "a member call does not match");

        TestRegistry.Assert(SourceSymbolParser.MatchDeclaration("        Increment(3);", "Increment", exact: true) is null, "a call is not a declaration");
        TestRegistry.Assert(SourceSymbolParser.MatchDeclaration("  function Increment() {}", "Increment", exact: true)?.Kind == "function", "the function is a declaration");
        return Task.CompletedTask;
    }

    private static Task DocumentationAsync()
    {
        const string code = "/// <summary>Counts.</summary>\n/// <remarks>More.</remarks>\npublic class Counter {}\n";
        var doc = SourceSymbolParser.ExtractDocumentation(code, 3);
        TestRegistry.Assert(doc == "<summary>Counts.</summary>\n<remarks>More.</remarks>", "joined doc comment, got: " + doc);
        TestRegistry.Assert(SourceSymbolParser.ExtractDocumentation(code, 1) is null, "no comment above the first line");
        return Task.CompletedTask;
    }
}
