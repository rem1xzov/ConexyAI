using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Repository;
using ConexyAI.Service.Admin;
using ConexyAI.Service.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConexyAI.Tests;

// ADMIN_ALERTS: добавлено 2026-11-09 — оплата и «позовите админа» в поддержке доходят до владельца
// push-уведомлением в мобильное приложение.
internal static class AdminAlertTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("admin alerts: a payment and a support escalation reach the owner's phone", AlertsReachAdminsAsync);
        TestRegistry.Add("admin alerts: no administrator accounts means no push", NoAdminsNoPushAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task AlertsReachAdminsAsync()
    {
        var (provider, push, adminId, buyerId) = await SetupAsync();

        var notifier = new AdminNotifier(
            provider.GetRequiredService<IServiceScopeFactory>(), push, NullLogger<AdminNotifier>.Instance);

        var payment = new PaymentEntity
        {
            UserId = buyerId,
            PlanId = "Pro",
            Tier = "Pro",
            Months = 1,
            AmountRub = 990,
            Kind = PaymentPlanKind.Subscription,
        };
        await notifier.PaymentAsync(buyerId, payment, "Pro");

        Assert(push.Sent.Count == 1, $"the owner gets exactly one push, got {push.Sent.Count}");
        var subscription = push.Sent[0];
        Assert(subscription.UserId == adminId, "the push goes to the administrator, not to the buyer");
        Assert(subscription.Data["type"] == "admin_payment", "the app recognises the payment alert");
        Assert(subscription.Data["title"].Contains("оплата", StringComparison.OrdinalIgnoreCase),
            $"the title says it is a payment, got '{subscription.Data["title"]}'");
        foreach (var expected in new[] { "buyer@example.com", "Pro", "990" })
        {
            Assert(subscription.Data["body"].Contains(expected),
                $"the body names the buyer, tier and amount (missing '{expected}'): {subscription.Data["body"]}");
        }

        var tokens = new PaymentEntity
        {
            UserId = buyerId,
            PlanId = "TokenCoder",
            Kind = PaymentPlanKind.Token,
            TokenAmount = 5_000_000,
            Pool = "Coder",
            AmountRub = 199,
        };
        await notifier.PaymentAsync(buyerId, tokens, null);

        Assert(push.Sent.Count == 2, "the token purchase is a separate alert");
        var tokenBody = push.Sent[1].Data["body"];
        Assert(push.Sent[1].Data["title"].Contains("токен", StringComparison.OrdinalIgnoreCase),
            $"the token title is recognisable, got '{push.Sent[1].Data["title"]}'");
        Assert(tokenBody.Contains("Coder") && tokenBody.Contains("199") && tokenBody.Contains("токенов"),
            $"the token body names the pool, amount of money and tokens: {tokenBody}");

        var ticketId = Guid.NewGuid();
        await notifier.SupportEscalatedAsync(buyerId, ticketId, "Не работает вход", "Бот не помог\nПозовите админа");

        Assert(push.Sent.Count == 3, "the support escalation is a separate alert");
        var support = push.Sent[2];
        Assert(support.Data["type"] == "admin_support", "the app recognises the support alert");
        Assert(support.Data["title"].Contains("админ", StringComparison.OrdinalIgnoreCase),
            $"the support title calls for an administrator, got '{support.Data["title"]}'");
        Assert(support.Data["body"].Contains("buyer@example.com")
               && support.Data["body"].Contains("Не работает вход")
               && support.Data["body"].Contains("Позовите админа"),
            $"the support body names the user, the topic and the last message: {support.Data["body"]}");
        Assert(support.Data["taskId"] == ticketId.ToString(), "the ticket id keeps separate notifications apart");
    }

    private static async Task NoAdminsNoPushAsync()
    {
        var (provider, push, adminId, buyerId) = await SetupAsync();
        provider.GetRequiredService<DbConexy>().Users.Single(u => u.Id == adminId).IsAdmin = false;
        await provider.GetRequiredService<DbConexy>().SaveChangesAsync();

        var notifier = new AdminNotifier(
            provider.GetRequiredService<IServiceScopeFactory>(), push, NullLogger<AdminNotifier>.Instance);

        await notifier.PaymentAsync(buyerId, new PaymentEntity { UserId = buyerId, Tier = "Go", AmountRub = 590 }, "Go");
        await notifier.SupportEscalatedAsync(buyerId, Guid.NewGuid(), "Тема", "текст");

        Assert(push.Sent.Count == 0, $"nobody to notify — nothing is sent, got {push.Sent.Count}");
    }

    private static async Task<(ServiceProvider Provider, FakePush Push, Guid AdminId, Guid BuyerId)> SetupAsync()
    {
        var dbName = "admin_alerts_" + Guid.NewGuid().ToString("N");
        var context = new DbConexy(new DbContextOptionsBuilder<DbConexy>().UseTestDatabase(dbName).Options);
        var adminId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        context.Users.Add(new User { Id = adminId, Email = "owner@example.com", EmailConfirmed = true, IsAdmin = true });
        context.Users.Add(new User { Id = buyerId, Email = "buyer@example.com", EmailConfirmed = true });
        await context.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddScoped<IUserRepository, UserRepository>();
        var provider = services.BuildServiceProvider();
        return (provider, new FakePush(), adminId, buyerId);
    }

    private sealed class FakePush : IPushService
    {
        public bool IsConfigured => true;

        public List<(Guid UserId, Dictionary<string, string> Data)> Sent { get; } = new();

        public Task<int> SendAsync(Guid userId, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
        {
            Sent.Add((userId, new Dictionary<string, string>(data)));
            return Task.FromResult(1);
        }
    }
}
