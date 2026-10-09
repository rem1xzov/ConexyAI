using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// SUPPORT_BOT: добавлено 2026-10-07
/// <summary>
/// Канал поддержки: бот отвечает по умолчанию, эскалация на оператора, «вернуться к боту» и
/// «отменить обращение», правило приоритета оператора. Логика на InMemory-БД и подставном LLM.
/// </summary>
internal static class SupportTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("support: a new ticket is bot-handled and the bot answers", BotAnswersAsync);
        TestRegistry.Add("support: escalation mutes the bot and adds a system line", EscalationMutesBotAsync);
        TestRegistry.Add("support: returning to the bot resumes it and clears the admin flag", ReturnToBotAsync);
        TestRegistry.Add("support: an admin message mutes the bot until it is returned to", AdminMutesBotAsync);
        TestRegistry.Add("support: cancel closes the ticket and blocks further messages", CancelClosesAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task BotAnswersAsync()
    {
        var (svc, llm, _, userId) = NewService();
        var ticket = await svc.GetOrCreateTicketAsync(userId);
        Assert(ticket.Status == "BotHandling" && ticket.BotActive, $"a fresh ticket must be bot-handled, got {ticket.Status}/{ticket.BotActive}");

        await svc.AddMessageAsync(ticket.Id, userId, "Как оплатить тариф?", isFromAdmin: false);

        var reloaded = await svc.GetMyTicketAsync(userId);
        var bot = reloaded!.Messages.SingleOrDefault(m => m.AuthorType == "Bot");
        Assert(bot is not null, "the bot must answer a user message");
        Assert(bot!.Content.Length > 0, "the bot reply must not be empty");
        Assert(llm.Calls == 1, $"the bot must be called once, got {llm.Calls}");
    }

    private static async Task EscalationMutesBotAsync()
    {
        var (svc, llm, _, userId) = NewService();
        var ticket = await svc.GetOrCreateTicketAsync(userId);
        await svc.AddMessageAsync(ticket.Id, userId, "первый вопрос", isFromAdmin: false);
        Assert(llm.Calls == 1, "the bot answered the first message");

        var escalated = await svc.EscalateAsync(ticket.Id, userId);
        Assert(escalated.Status == "Escalated" && !escalated.BotActive,
            $"escalation must mute the bot, got {escalated.Status}/{escalated.BotActive}");
        Assert(escalated.Messages.Any(m => m.AuthorType == "System"), "escalation must add a system message");

        await svc.AddMessageAsync(ticket.Id, userId, "второй вопрос", isFromAdmin: false);
        Assert(llm.Calls == 1, $"the bot must stay silent after escalation, got {llm.Calls} calls");
    }

    private static async Task ReturnToBotAsync()
    {
        var (svc, llm, _, userId) = NewService();
        var ticket = await svc.GetOrCreateTicketAsync(userId);
        await svc.EscalateAsync(ticket.Id, userId);

        var returned = await svc.ReturnToBotAsync(ticket.Id, userId);
        Assert(returned.BotActive && !returned.Messages.Any(m => m.AuthorType is "Bot") && returned.Status == "Escalated",
            "returning keeps the ticket escalated but wakes the bot");
        Assert(returned.Messages.Count(m => m.AuthorType == "System") >= 2, "return adds its own system line");

        await svc.AddMessageAsync(ticket.Id, userId, "снова к боту", isFromAdmin: false);
        Assert(llm.Calls == 1, $"the bot must reply again after returning, got {llm.Calls} calls");
    }

    private static async Task AdminMutesBotAsync()
    {
        var (svc, llm, _, userId) = NewService();
        var adminId = Guid.NewGuid();
        var ticket = await svc.GetOrCreateTicketAsync(userId);
        await svc.EscalateAsync(ticket.Id, userId);

        // Оператор написал — правило 8: бот молчит.
        await svc.AddMessageAsync(ticket.Id, adminId, "Здравствуйте, я оператор.", isFromAdmin: true);
        var afterAdmin = await svc.GetAdminTicketAsync(ticket.Id);
        Assert(!afterAdmin!.BotActive, "an admin message must mute the bot");

        await svc.AddMessageAsync(ticket.Id, userId, "ответ пользователя", isFromAdmin: false);
        Assert(llm.Calls == 0, $"the bot must not answer while the admin is active, got {llm.Calls} calls");

        // Возврат к боту сбрасывает флаг оператора.
        await svc.ReturnToBotAsync(ticket.Id, userId);
        await svc.AddMessageAsync(ticket.Id, userId, "ещё вопрос", isFromAdmin: false);
        Assert(llm.Calls == 1, $"the bot resumes after returning, got {llm.Calls} calls");
    }

    private static async Task CancelClosesAsync()
    {
        var (svc, _, _, userId) = NewService();
        var ticket = await svc.GetOrCreateTicketAsync(userId);
        await svc.EscalateAsync(ticket.Id, userId);

        var closed = await svc.CancelAsync(ticket.Id, userId);
        Assert(closed.Status == "Closed" && closed.ClosedAt is not null, "cancel must close the ticket");
        Assert(closed.Messages.Any(m => m.AuthorType == "System" && m.Content.Contains("закрыт")),
            "cancel must add a closing system message");

        // В закрытое обращение писать нельзя — пользователь открывает новое.
        var threw = false;
        try
        {
            await svc.AddMessageAsync(ticket.Id, userId, "ещё", isFromAdmin: false);
        }
        catch (AuthException)
        {
            threw = true;
        }
        Assert(threw, "writing to a closed ticket must be refused");

        // Следующая поддержка создаёт НОВЫЙ тикет.
        var next = await svc.GetOrCreateTicketAsync(userId);
        Assert(next.Id != ticket.Id && next.Status == "BotHandling",
            "after closing, a new ticket is created");
    }

    private static (SupportService Service, FakeSupportLlm Llm, RecordingSupportHub Hub, Guid UserId) NewService()
    {
        var dbName = "support_" + Guid.NewGuid().ToString("N");
        var context = new DbConexy(new DbContextOptionsBuilder<DbConexy>().UseInMemoryDatabase(dbName).Options);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, Email = $"{userId:N}@example.com", EmailConfirmed = true });
        context.SaveChanges();

        var llm = new FakeSupportLlm();
        var hub = new RecordingSupportHub();
        var service = new SupportService(new SupportRepository(context), hub, llm, NullLogger<SupportService>.Instance);
        return (service, llm, hub, userId);
    }

    /// <summary>Подставной LLM: всегда возвращает заранее заданный текст и считает вызовы.</summary>
    private sealed class FakeSupportLlm : IConexyLlmClient
    {
        public int Calls { get; private set; }
        public string Reply { get; set; } = "Здравствуйте! Подскажу по тарифам и лимитам.";

        public Task<LlmChatResult> SendChatAsync(
            ConexyModelType modelType, List<ChatMessage> messages, List<object> tools,
            string? reasoningEffort = null, Guid? taskId = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LlmChatResult(new ChatMessage("assistant", Reply), 10));
        }

        public IAsyncEnumerable<StreamDelta> StreamChatAsync(
            List<ChatMessage> messages, ConexyModelType modelType, string? reasoningEffort = null,
            List<object>? tools = null, string? toolChoice = null, Guid? taskId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Минимальный IHubContext: запоминает отправленные в группу сообщения.</summary>
    private sealed class RecordingSupportHub : IHubContext<ConexyHub>
    {
        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new NoGroups();

        public RecordingSupportHub() => Clients = new RecordingClients();

        private sealed class RecordingClients : IHubClients
        {
            private readonly RecordingProxy _proxy = new();
            public IClientProxy All => _proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
            public IClientProxy Client(string connectionId) => _proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
            public IClientProxy Group(string groupName) => _proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
            public IClientProxy User(string userId) => _proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
        }

        private sealed class RecordingProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class NoGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
