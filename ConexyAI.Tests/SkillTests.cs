using System.Runtime.CompilerServices;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service;
using Microsoft.EntityFrameworkCore;

// AGENT_SKILLS: добавлено 2026-10-18
/// <summary>Скиллы агента: разбор SKILL.md, замена по имени, отключение, доступ только своему пользователю.</summary>
internal static class SkillTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("skills: SKILL.md frontmatter gives name and description", ParseFrontmatterAsync);
        TestRegistry.Add("skills: a plain markdown file is named after its heading or file", ParsePlainAsync);
        TestRegistry.Add("skills: upsert by name, disable, and per-user isolation", StoreAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task ParseFrontmatterAsync()
    {
        await Task.CompletedTask;
        var (name, description, content) = SkillService.Parse("SKILL.md",
            "---\nname: Code Review\ndescription: \"Когда просят ревью PR\"\n---\n# Ревью\n\nПроверяй тесты.");
        Assert(name == "code-review", $"name is slugged, got '{name}'");
        Assert(description == "Когда просят ревью PR", $"description from frontmatter, got '{description}'");
        Assert(content.StartsWith("# Ревью") && !content.Contains("---"), "frontmatter is not part of the body");
    }

    private static async Task ParsePlainAsync()
    {
        await Task.CompletedTask;
        var (name, description, _) = SkillService.Parse("SKILL.md", "# Отчёт по продажам\n\nСобирай отчёт в Excel по шаблону.\n\n## Шаги\n1. ...");
        Assert(name == "otchet-po-prodazham", $"a SKILL.md without frontmatter is named after its heading, got '{name}'");
        Assert(description == "Собирай отчёт в Excel по шаблону.", $"description is the first paragraph, got '{description}'");
        Assert(SkillService.Parse("deploy-rules.md", "Деплой только через CI.").Name == "deploy-rules", "otherwise the file name");

        var refused = false;
        try { SkillService.Parse("x.md", "   "); }
        catch (AuthException) { refused = true; }
        Assert(refused, "an empty skill is refused");
    }

    private static async Task StoreAsync()
    {
        var context = new DbConexy(new DbContextOptionsBuilder<DbConexy>().UseTestDatabase("skills_" + Guid.NewGuid().ToString("N")).Options);
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        context.Users.Add(new User { Id = me, Email = "me@example.com", EmailConfirmed = true });
        context.Users.Add(new User { Id = other, Email = "other@example.com", EmailConfirmed = true });
        await context.SaveChangesAsync();
        var svc = new SkillService(context);

        var first = await svc.UpsertFromFileAsync(me, "SKILL.md", "---\nname: style\ndescription: v1\n---\nТабы.");
        var second = await svc.UpsertFromFileAsync(me, "SKILL.md", "---\nname: style\ndescription: v2\n---\nПробелы.");
        Assert(first.Id == second.Id && (await svc.ListAsync(me)).Count == 1, "the same name replaces the skill instead of duplicating it");
        Assert(await svc.ReadAsync(me, "style") == "Пробелы.", "the newest text is served");
        Assert(await svc.ReadAsync(other, "style") is null, "another user cannot read my skill");

        await svc.SetEnabledAsync(me, first.Id, false);
        Assert((await svc.EnabledSummariesAsync(me)).Count == 0, "a disabled skill is not offered to the agent");
        Assert(await svc.ReadAsync(me, "style") is null, "a disabled skill cannot be read");
        Assert(!await svc.DeleteAsync(other, first.Id), "another user cannot delete my skill");
        Assert(await svc.DeleteAsync(me, first.Id) && (await svc.ListAsync(me)).Count == 0, "the owner can delete it");
    }
}
