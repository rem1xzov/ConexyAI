using System.Runtime.CompilerServices;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// ADMIN_TOKEN_CREDIT: добавлено 2026-11-19
/// <summary>
/// Ручное начисление токенов админом: попадание в нужный пул, суммирование, раздельность пулов,
/// журнал операций и отказ на некорректных входных данных. БД — в памяти.
/// </summary>
internal static class AdminTokenCreditTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("admin tokens: credit adds to the chosen pool and logs the grant", CreditAsync);
        TestRegistry.Add("admin tokens: repeated credits stack", StackAsync);
        TestRegistry.Add("admin tokens: Coder and Cowork stay separate", PoolsAsync);
        TestRegistry.Add("admin tokens: invalid pool, amount and unknown user are rejected", RejectsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static DbConexy NewContext(string name) =>
        new(new DbContextOptionsBuilder<DbConexy>().UseTestDatabase(name).Options);

    private static AdminTokenService NewService(DbConexy context) =>
        new(context, NullLogger<AdminTokenService>.Instance);

    private static async Task CreditAsync()
    {
        var name = "grant_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, Email = "user@example.com" });
        await context.SaveChangesAsync();

        var credited = await NewService(context).CreditAsync(userId, adminId, "Coder", 500_000, "компенсация");

        Assert(credited, "the existing user must be credited");
        var user = await context.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert(user.CoderTokenTopUp == 500_000, $"the Coder pool must gain 500k, got {user.CoderTokenTopUp}");
        Assert(user.CoworkTokenTopUp == 0, "a Coder credit must not touch the Cowork pool");

        var grant = await context.AdminTokenGrants.AsNoTracking().SingleAsync();
        Assert(grant.UserId == userId && grant.AdminId == adminId, "the grant must record both ids");
        Assert(grant.Pool == "Coder" && grant.Amount == 500_000, "the grant must record pool and amount");
        Assert(grant.Reason == "компенсация", "the reason must be stored");
    }

    private static async Task StackAsync()
    {
        var name = "grant_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId });
        await context.SaveChangesAsync();

        var service = NewService(context);
        await service.CreditAsync(userId, adminId, "Coder", 300, null);
        await service.CreditAsync(userId, adminId, "Coder", 700, null);

        var user = await context.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert(user.CoderTokenTopUp == 1000, $"credits must stack, got {user.CoderTokenTopUp}");
        Assert(await context.AdminTokenGrants.CountAsync() == 2, "each credit is one journal row");
    }

    private static async Task PoolsAsync()
    {
        var name = "grant_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId });
        await context.SaveChangesAsync();

        var service = NewService(context);
        await service.CreditAsync(userId, adminId, "Cowork", 42, null);

        var user = await context.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert(user.CoworkTokenTopUp == 42 && user.CoderTokenTopUp == 0, "the Cowork credit stays in its pool");
    }

    private static async Task RejectsAsync()
    {
        var name = "grant_" + Guid.NewGuid().ToString("N");
        await using var context = NewContext(name);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId });
        await context.SaveChangesAsync();
        var service = NewService(context);
        var adminId = Guid.NewGuid();

        await AssertAuthAsync(
            () => service.CreditAsync(userId, adminId, "Gold", 10, null),
            "invalid_pool",
            "an unknown pool must be rejected");
        await AssertAuthAsync(
            () => service.CreditAsync(userId, adminId, "Coder", 0, null),
            "invalid_amount",
            "a zero amount must be rejected");
        await AssertAuthAsync(
            () => service.CreditAsync(userId, adminId, "Coder", -5, null),
            "invalid_amount",
            "a negative amount must be rejected");

        var unknown = await service.CreditAsync(Guid.NewGuid(), adminId, "Coder", 10, null);
        Assert(!unknown, "an unknown user must not be reported as credited");
        Assert(await context.AdminTokenGrants.CountAsync() == 0, "rejected calls write no journal row");
    }

    private static async Task AssertAuthAsync(Func<Task> body, string code, string message)
    {
        try
        {
            await body();
        }
        catch (AuthException ex)
        {
            Assert(ex.Code == code, $"{message}: expected code {code}, got {ex.Code}");
            return;
        }

        throw new InvalidOperationException($"Assertion failed: {message} (no exception)");
    }
}
