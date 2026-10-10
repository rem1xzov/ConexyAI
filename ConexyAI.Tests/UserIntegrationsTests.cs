using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// USER_INTEGRATIONS: добавлено 2026-10-10 — креды агента (GitHub PAT, MCP) хранятся на сервере
// зашифрованными. Тесты проверяют шифрование и сквозное сохранение/чтение.
internal static class UserIntegrationsTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("integrations: secrets are encrypted at rest and decrypt back", ProtectorRoundTripAsync);
        TestRegistry.Add("integrations: GitHub token and MCP servers persist server-side", ServiceRoundTripAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static ISecretProtector NewProtector() =>
        new AesSecretProtector(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "unit-test-signing-key-that-is-long-enough-for-sha256",
            }).Build(),
            NullLogger<AesSecretProtector>.Instance);

    private static Task ProtectorRoundTripAsync()
    {
        var protector = NewProtector();
        const string secret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

        var cipher = protector.Protect(secret);
        Assert(cipher is not null, "a non-empty secret must encrypt");
        Assert(cipher != secret && !cipher!.Contains(secret), "the ciphertext must not contain the plaintext");
        Assert(protector.Unprotect(cipher) == secret, "decryption must return the original secret");

        // Порча шифротекста (или чужой ключ) даёт null, а не исключение.
        var tampered = cipher[..^4] + "AAAA";
        Assert(protector.Unprotect(tampered) is null, "a tampered ciphertext must not decrypt");

        Assert(protector.Protect("") is null, "empty input stays empty");
        Assert(protector.Unprotect(null) is null, "null stays null");
        return Task.CompletedTask;
    }

    private static async Task ServiceRoundTripAsync()
    {
        var dbName = "integrations_" + Guid.NewGuid().ToString("N");
        var context = new DbConexy(new DbContextOptionsBuilder<DbConexy>().UseInMemoryDatabase(dbName).Options);
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, Email = $"{userId:N}@example.com", EmailConfirmed = true });
        await context.SaveChangesAsync();

        var service = new UserIntegrationsService(context, NewProtector());

        Assert(!await service.HasGitHubTokenAsync(userId), "no token before saving");

        await service.SaveGitHubTokenAsync(userId, "ghp_token_value_123");
        Assert(await service.HasGitHubTokenAsync(userId), "token is present after saving");
        Assert((await service.GetForRunAsync(userId)).GitHubToken == "ghp_token_value_123", "the run gets the token back");

        // В БД токен лежит зашифрованным, а не открытым текстом.
        var stored = context.UserIntegrations.AsNoTracking().First(x => x.UserId == userId).GitHubToken;
        Assert(stored is not null && !stored.Contains("ghp_token_value_123"), "the raw column must not hold the token");

        await service.ClearGitHubTokenAsync(userId);
        Assert(!await service.HasGitHubTokenAsync(userId), "clearing removes the token");

        var servers = new List<McpServerInput>
        {
            new() { Id = "notion", Name = "Notion", Url = "https://mcp.notion.com/mcp", Token = "secret-mcp", Enabled = true },
        };
        await service.SaveMcpServersAsync(userId, servers);

        var read = await service.GetForRunAsync(userId);
        Assert(read.McpServers.Count == 1
               && read.McpServers[0].Url == "https://mcp.notion.com/mcp"
               && read.McpServers[0].Token == "secret-mcp", "MCP servers round-trip with their token");
    }
}
