using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// SHARE_TO_ADMIN: добавлено 2026-11-19
/// <summary>
/// «Поделиться чатом с админом» и журнал действий агента: владелец создаёт/обновляет жалобу со
/// скриншотом, посторонний и незахваченный чат отклоняются, админ видит список, диалог, действия
/// агента и картинку. БД — в памяти.
/// </summary>
internal static class ChatShareTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("share to admin: owner share stores comment, title and screenshot", ShareAsync);
        TestRegistry.Add("share to admin: a non-owner and an unclaimed chat are refused", RefusesAsync);
        TestRegistry.Add("share to admin: re-share updates one report and reopens it", ReShareAsync);
        TestRegistry.Add("share to admin: validation rejects long comments, wrong types and huge screenshots", ValidationAsync);
        TestRegistry.Add("share to admin: admin list filters, searches and counts agent events", ListAsync);
        TestRegistry.Add("share to admin: admin detail returns transcript, actions and screenshot", DetailAsync);
        TestRegistry.Add("share to admin: close and revoke", CloseAndRevokeAsync);
        TestRegistry.Add("agent log: records tool rows with increasing seq and masks images", LogRecordsAsync);
        TestRegistry.Add("agent log: keeps only the newest actions per chat", LogBoundsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static DbConexy NewContext(string name) =>
        new(new DbContextOptionsBuilder<DbConexy>().UseTestDatabase(name).Options);

    private static IChatShareService NewShares(DbConexy context) =>
        new ChatShareService(context, NullLogger<ChatShareService>.Instance);

    private static async Task<(Guid UserId, Guid ChatId)> SeedChatAsync(DbConexy context, string email = "user@example.com")
    {
        var userId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, Email = email, GitHubUsername = "octocat" });
        context.Chats.Add(new ChatEntity { Id = chatId, UserId = userId, Kind = "chat" });
        context.ChatMessages.Add(new ConexyChatMessageEntity
        {
            ChatId = chatId,
            UserId = userId,
            Role = "user",
            Content = "Агент сломал верстку на главной странице",
            Kind = "chat",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        });
        context.ChatMessages.Add(new ConexyChatMessageEntity
        {
            ChatId = chatId,
            UserId = userId,
            Role = "assistant",
            Content = "Готово, задача выполнена.",
            Kind = "chat",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        });
        await context.SaveChangesAsync();
        return (userId, chatId);
    }

    private static async Task ShareAsync()
    {
        var name = "share_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var (userId, chatId) = await SeedChatAsync(context);
        var png = new byte[] { 1, 2, 3, 4 };

        var response = await NewShares(context).ShareAsync(userId, chatId, new ShareToAdminRequest(
            "Кнопка уехала", "bug.png", "image/png", Convert.ToBase64String(png)));

        Assert(response.Status == ChatShareStatus.Open, "a fresh share is open");
        var report = await context.ChatShareReports.AsNoTracking().SingleAsync();
        Assert(report.Id == response.Id && report.ChatId == chatId && report.UserId == userId, "the report keys must match");
        Assert(report.Comment == "Кнопка уехала", "the comment must be stored");
        Assert(report.ChatTitle == "Агент сломал верстку на главной странице", $"title snapshot, got '{report.ChatTitle}'");
        Assert(report.Screenshot is { Length: 4 } && report.ScreenshotContentType == "image/png", "the screenshot must be stored");
        Assert(report.ScreenshotFileName == "bug.png", "the file name must be stored");
    }

    private static async Task RefusesAsync()
    {
        var name = "share_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var (_, chatId) = await SeedChatAsync(context);
        var shares = NewShares(context);

        await AssertAuthAsync(
            () => shares.ShareAsync(Guid.NewGuid(), chatId, new ShareToAdminRequest(null, null, null, null)),
            "chat_not_found",
            "a stranger must not share someone else's chat");
        await AssertAuthAsync(
            () => shares.ShareAsync(Guid.NewGuid(), Guid.NewGuid(), new ShareToAdminRequest(null, null, null, null)),
            "chat_not_ready",
            "an unclaimed chat must be refused");
        Assert(await context.ChatShareReports.CountAsync() == 0, "refused shares write nothing");
    }

    private static async Task ReShareAsync()
    {
        var name = "share_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var (userId, chatId) = await SeedChatAsync(context);
        var shares = NewShares(context);

        var first = await shares.ShareAsync(userId, chatId, new ShareToAdminRequest("первый", null, null, null));
        await shares.CloseAsync(first.Id, "fixed");
        var second = await shares.ShareAsync(userId, chatId, new ShareToAdminRequest("второй", null, null, null));

        Assert(first.Id == second.Id, "re-sharing must reuse the same report row");
        Assert(await context.ChatShareReports.CountAsync() == 1, "one report per chat");
        var report = await context.ChatShareReports.AsNoTracking().SingleAsync();
        Assert(report.Comment == "второй", "the comment must be refreshed");
        Assert(report.Status == ChatShareStatus.Open && report.ClosedAt is null, "a new complaint reopens the report");
    }

    private static async Task ValidationAsync()
    {
        var name = "share_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var (userId, chatId) = await SeedChatAsync(context);
        var shares = NewShares(context);

        await AssertAuthAsync(
            () => shares.ShareAsync(userId, chatId, new ShareToAdminRequest(new string('x', 2001), null, null, null)),
            "comment_too_long",
            "a too long comment is refused");
        await AssertAuthAsync(
            () => shares.ShareAsync(userId, chatId, new ShareToAdminRequest(null, "a.txt", "text/plain", "AAAA")),
            "unsupported_screenshot",
            "a non-image screenshot is refused");

        var huge = Convert.ToBase64String(new byte[ChatShareService.MaxScreenshotBytes + 1]);
        await AssertAuthAsync(
            () => shares.ShareAsync(userId, chatId, new ShareToAdminRequest(null, "big.png", "image/png", huge)),
            "screenshot_too_large",
            "an oversized screenshot is refused");
        Assert(await context.ChatShareReports.CountAsync() == 0, "invalid shares write nothing");
    }

    private static async Task ListAsync()
    {
        var name = "list_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var (userA, chatA) = await SeedChatAsync(context, "a@example.com");
        var (userB, chatB) = await SeedChatAsync(context, "b@example.com");
        var shares = NewShares(context);
        var reportA = await shares.ShareAsync(userA, chatA, new ShareToAdminRequest("баг A", null, null, null));
        await shares.ShareAsync(userB, chatB, new ShareToAdminRequest("баг B", null, null, null));
        await shares.CloseAsync(reportA.Id, null);

        context.ChatAgentEvents.AddRange(
            new ChatAgentEventEntity { ChatId = chatB, Kind = "tool", Tool = "bash", Seq = 1 },
            new ChatAgentEventEntity { ChatId = chatB, Kind = "tool", Tool = "bash", Seq = 2 });
        await context.SaveChangesAsync();

        var all = await shares.ListAsync(1, 20, null, null);
        Assert(all.TotalCount == 2 && all.Items.Count == 2, $"both reports, got {all.TotalCount}");

        var open = await shares.ListAsync(1, 20, "open", null);
        Assert(open.TotalCount == 1 && open.Items[0].UserEmail == "b@example.com", "the open filter must work");
        Assert(open.Items[0].AgentEventCount == 2, $"agent actions must be counted, got {open.Items[0].AgentEventCount}");

        var closed = await shares.ListAsync(1, 20, "closed", null);
        Assert(closed.TotalCount == 1 && closed.Items[0].Comment == "баг A", "the closed filter must work");

        var found = await shares.ListAsync(1, 20, null, "a@example.com");
        Assert(found.TotalCount == 1 && found.Items[0].UserEmail == "a@example.com", "search by email must work");

        var page = await shares.ListAsync(2, 1, null, null);
        Assert(page.TotalCount == 2 && page.Items.Count == 1, "pagination must work");
    }

    private static async Task DetailAsync()
    {
        var name = "detail_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var (userId, chatId) = await SeedChatAsync(context);
        var shares = NewShares(context);
        var png = new byte[] { 9, 8, 7 };
        var created = await shares.ShareAsync(userId, chatId, new ShareToAdminRequest(
            "визуальный баг", "v.png", "image/jpeg", Convert.ToBase64String(png)));

        context.ChatAgentEvents.AddRange(
            new ChatAgentEventEntity { ChatId = chatId, Kind = "tool", Tool = "bash", Arguments = "{\"command\":\"dotnet build\"}", Output = "error CS1002", IsError = true, Seq = 2 },
            new ChatAgentEventEntity { ChatId = chatId, Kind = "tool", Tool = "file_read", Arguments = "{}", Output = "ok", Seq = 1 });
        await context.SaveChangesAsync();

        var detail = await shares.GetAsync(created.Id);
        Assert(detail is not null, "the detail must exist");
        Assert(detail!.UserEmail == "user@example.com" && detail.UserGitHubUsername == "octocat", "the owner identity");
        Assert(detail.Messages.Count == 2 && detail.Messages[0].Role == "user", "the transcript must be ordered");
        Assert(detail.AgentEvents.Count == 2 && detail.AgentEvents[0].Seq == 1, "agent actions must be ordered by seq");
        Assert(detail.AgentEvents[1].IsError && detail.AgentEvents[1].Output == "error CS1002", "the failing action must survive");
        Assert(detail.ScreenshotBase64 == Convert.ToBase64String(png) && detail.ScreenshotContentType == "image/jpeg",
            "the screenshot must round-trip");
        Assert(await shares.GetAsync(Guid.NewGuid()) is null, "an unknown report is null");
    }

    private static async Task CloseAndRevokeAsync()
    {
        var name = "close_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var (userId, chatId) = await SeedChatAsync(context);
        var shares = NewShares(context);
        var created = await shares.ShareAsync(userId, chatId, new ShareToAdminRequest("баг", null, null, null));

        Assert(await shares.CloseAsync(created.Id, "исправлено в 0.9.45"), "close must succeed");
        var detail = await shares.GetAsync(created.Id);
        Assert(detail!.Status == ChatShareStatus.Closed, "the status must be closed");
        Assert(detail.AdminNote == "исправлено в 0.9.45", "the admin note must be stored");
        Assert(await shares.CloseAsync(Guid.NewGuid(), null) == false, "closing an unknown report returns false");

        Assert(await shares.RevokeAsync(userId, chatId), "the owner must be able to revoke");
        Assert(await context.ChatShareReports.CountAsync() == 0, "revoke deletes the row");
        Assert(await shares.RevokeAsync(userId, chatId) == false, "a second revoke returns false");
    }

    private static async Task LogRecordsAsync()
    {
        var name = "log_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var chatId = Guid.NewGuid();
        var log = new ChatAgentLog(context);

        await log.RecordToolAsync(chatId, Guid.NewGuid(), "bash", "{\"command\":\"ls\"}", "a.txt", false);
        await log.RecordToolAsync(chatId, Guid.NewGuid(), "screen_capture", "{}", "IMAGE_BASE64:image/png:" + new string('A', 4096), false);
        var longOutput = new string('z', 10_000);
        await log.RecordToolAsync(chatId, Guid.NewGuid(), "bash", "{\"command\":\"cat big\"}", longOutput, true);

        var rows = await context.ChatAgentEvents.AsNoTracking().OrderBy(e => e.Seq).ToListAsync();
        Assert(rows.Count == 3, $"three actions, got {rows.Count}");
        Assert(rows[0].Seq == 1 && rows[2].Seq == 3, "seq must increase");
        Assert(rows[1].Output!.StartsWith("[изображение", StringComparison.Ordinal), "images are masked: " + rows[1].Output);
        Assert(rows[2].Output!.Length <= 8002 && rows[2].IsError, "long output is bounded and the error flag survives");
    }

    private static async Task LogBoundsAsync()
    {
        var name = "log_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var chatId = Guid.NewGuid();
        var log = new ChatAgentLog(context);

        var total = ChatAgentLog.MaxPerChat + 5;
        for (var i = 0; i < total; i++)
            await log.RecordToolAsync(chatId, null, "bash", "{}", $"out {i}", false);

        var count = await context.ChatAgentEvents.CountAsync(e => e.ChatId == chatId);
        Assert(count == ChatAgentLog.MaxPerChat, $"the log is bounded to {ChatAgentLog.MaxPerChat}, got {count}");
        var rows = await context.ChatAgentEvents.AsNoTracking().OrderBy(e => e.Seq).ToListAsync();
        Assert(rows[0].Seq == 6 && rows[^1].Seq == total, "the newest actions are kept");
    }

    private static async Task AssertAuthAsync(Func<Task> body, string code, string message)
    {
        try
        {
            await body();
        }
        catch (AuthException ex)
        {
            Assert(ex.Code == code, $"{message}: expected {code}, got {ex.Code}");
            return;
        }

        throw new InvalidOperationException($"Assertion failed: {message} (no exception)");
    }
}
