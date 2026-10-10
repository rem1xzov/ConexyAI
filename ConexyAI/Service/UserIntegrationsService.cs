using System.Text.Json;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Service;

// USER_INTEGRATIONS: добавлено 2026-10-10 — доступ к персональным кредам агента, сохранённым на
// сервере. Наружу отдаём либо статус («настроено»), либо расшифрованные значения владельцу; в БД
// они лежат зашифрованными (ISecretProtector).
public sealed record StoredIntegrations(string? GitHubToken, IReadOnlyList<McpServerInput> McpServers);

public interface IUserIntegrationsService
{
    /// <summary>Сохранён ли у пользователя GitHub-токен (без расшифровки значения наружу).</summary>
    Task<bool> HasGitHubTokenAsync(Guid userId, CancellationToken ct = default);

    Task SaveGitHubTokenAsync(Guid userId, string token, CancellationToken ct = default);

    Task ClearGitHubTokenAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<McpServerInput>> GetMcpServersAsync(Guid userId, CancellationToken ct = default);

    Task SaveMcpServersAsync(Guid userId, IReadOnlyList<McpServerInput> servers, CancellationToken ct = default);

    /// <summary>Расшифрованные креды для запуска задачи агента (фолбэк, когда запрос их не принёс).</summary>
    Task<StoredIntegrations> GetForRunAsync(Guid userId, CancellationToken ct = default);
}

public class UserIntegrationsService : IUserIntegrationsService
{
    /// <summary>Столько же личных MCP-серверов разрешает реестр MCP на прогон.</summary>
    public const int MaxMcpServers = 10;

    private readonly DbConexy _db;
    private readonly ISecretProtector _protector;

    public UserIntegrationsService(DbConexy db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<bool> HasGitHubTokenAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _db.UserIntegrations.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return !string.IsNullOrEmpty(_protector.Unprotect(row?.GitHubToken));
    }

    public async Task SaveGitHubTokenAsync(Guid userId, string token, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(userId, ct);
        row.GitHubToken = _protector.Protect(token.Trim());
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearGitHubTokenAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _db.UserIntegrations.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null)
            return;

        row.GitHubToken = null;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<McpServerInput>> GetMcpServersAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _db.UserIntegrations.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return DecodeServers(_protector.Unprotect(row?.McpServers));
    }

    public async Task SaveMcpServersAsync(Guid userId, IReadOnlyList<McpServerInput> servers, CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(userId, ct);
        row.McpServers = _protector.Protect(EncodeServers(servers));
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<StoredIntegrations> GetForRunAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _db.UserIntegrations.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null)
            return new StoredIntegrations(null, Array.Empty<McpServerInput>());

        return new StoredIntegrations(
            _protector.Unprotect(row.GitHubToken),
            DecodeServers(_protector.Unprotect(row.McpServers)));
    }

    private async Task<UserIntegrationsEntity> GetOrCreateAsync(Guid userId, CancellationToken ct)
    {
        var row = await _db.UserIntegrations.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is not null)
            return row;

        row = new UserIntegrationsEntity { UserId = userId };
        _db.UserIntegrations.Add(row);
        return row;
    }

    private static string? EncodeServers(IReadOnlyList<McpServerInput> servers)
    {
        var clean = servers
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Url))
            .Take(MaxMcpServers)
            .Select(s => new McpServerInput
            {
                Id = Trim(s.Id),
                Name = Trim(s.Name),
                Url = s.Url!.Trim(),
                Token = Trim(s.Token),
                Enabled = s.Enabled,
            })
            .ToList();

        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    private static IReadOnlyList<McpServerInput> DecodeServers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<McpServerInput>();

        try
        {
            return JsonSerializer.Deserialize<List<McpServerInput>>(json) ?? new List<McpServerInput>();
        }
        catch (JsonException)
        {
            return Array.Empty<McpServerInput>();
        }
    }

    private static string? Trim(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
