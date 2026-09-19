using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Per-user subscription limits and usage tracking. Windows reset lazily on first access
/// after their reset timestamp; flash/pro counters count requests, the agent counter
/// accumulates DeepSeek <c>usage.total_tokens</c>.
/// </summary>
public interface ISubscriptionService
{
    Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken ct = default);
    Task<UsageDecision> CheckBeforeRunAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default);
    Task RecordRequestAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default);
    Task RecordAgentTokensAsync(Guid userId, long tokens, CancellationToken ct = default);
}

public class SubscriptionService : ISubscriptionService
{
    private readonly IUsageRepository _repository;
    // ADMIN_UNLIMITED: добавлено 2026-09-19
    private readonly IUserRepository _userRepository;
    private readonly IOptions<SubscriptionLimitsOptions> _options;

    public SubscriptionService(IUsageRepository repository, IUserRepository userRepository, IOptions<SubscriptionLimitsOptions> options)
    {
        _repository = repository;
        _userRepository = userRepository;
        _options = options;
    }

    public async Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken ct = default)
    {
        // ADMIN_UNLIMITED: добавлено 2026-09-19 — admins have no limits and never create a
        // UserUsageCounter row, so return a synthetic "Admin" snapshot without touching the DB.
        if (await IsAdminAsync(userId, ct))
            return AdminUsage();

        var counter = await GetOrCreateAsync(userId, ct);
        var limits = GetTierLimits(counter.Tier);
        return new SubscriptionUsageDto(
            counter.Tier.ToString(),
            counter.FlashRequestsUsed, limits.FlashRequestsPerWindow, counter.FlashWindowResetAt,
            counter.ProRequestsUsed, limits.ProRequestsPerWindow, counter.ProWindowResetAt,
            counter.AgentTokensUsed, limits.AgentTokenBudget, counter.AgentWindowResetAt);
    }

    public async Task<UsageDecision> CheckBeforeRunAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default)
    {
        // ADMIN_UNLIMITED: добавлено 2026-09-19 — admins bypass every limit and never read or
        // increment the UserUsageCounter.
        if (await IsAdminAsync(userId, ct))
            return new UsageDecision(UsageDecisionKind.Allowed);

        var counter = await GetOrCreateAsync(userId, ct);
        var limits = GetTierLimits(counter.Tier);

        switch (modelType)
        {
            case ConexyModelType.ConexyV1Flash:
                if (counter.FlashRequestsUsed >= limits.FlashRequestsPerWindow)
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, "flash", counter.FlashWindowResetAt);
                return new UsageDecision(UsageDecisionKind.Allowed);

            case ConexyModelType.ConexyV1Pro:
                if (counter.ProRequestsUsed >= limits.ProRequestsPerWindow)
                {
                    // Transparent fallback to flash if the flash budget still has room.
                    if (counter.FlashRequestsUsed < limits.FlashRequestsPerWindow)
                        return new UsageDecision(UsageDecisionKind.FallbackToFlash);
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, "pro", counter.ProWindowResetAt);
                }
                return new UsageDecision(UsageDecisionKind.Allowed);

            case ConexyModelType.ConexyCoder:
                if (counter.AgentTokensUsed >= limits.AgentTokenBudget)
                    return new UsageDecision(UsageDecisionKind.LimitExceeded, "agent", counter.AgentWindowResetAt);
                return new UsageDecision(UsageDecisionKind.Allowed);

            default:
                return new UsageDecision(UsageDecisionKind.Allowed);
        }
    }

    public async Task RecordRequestAsync(Guid userId, ConexyModelType modelType, CancellationToken ct = default)
    {
        // ADMIN_UNLIMITED: добавлено 2026-09-19 — admins never accrue usage.
        if (await IsAdminAsync(userId, ct))
            return;

        var counter = await GetOrCreateAsync(userId, ct);
        if (modelType == ConexyModelType.ConexyV1Pro)
            counter.ProRequestsUsed++;
        else if (modelType == ConexyModelType.ConexyV1Flash)
            counter.FlashRequestsUsed++;
        await _repository.UpsertAsync(counter, ct);
    }

    public async Task RecordAgentTokensAsync(Guid userId, long tokens, CancellationToken ct = default)
    {
        if (tokens <= 0) return;

        // ADMIN_UNLIMITED: добавлено 2026-09-19 — admins never accrue usage.
        if (await IsAdminAsync(userId, ct))
            return;

        var counter = await GetOrCreateAsync(userId, ct);
        counter.AgentTokensUsed += tokens;
        await _repository.UpsertAsync(counter, ct);
    }

    private async Task<UserUsageCounterEntity> GetOrCreateAsync(Guid userId, CancellationToken ct)
    {
        var counter = await _repository.GetAsync(userId, ct);
        var now = DateTime.UtcNow;

        if (counter is null)
        {
            counter = NewCounter(userId, now);
            await _repository.UpsertAsync(counter, ct);
            return counter;
        }

        // Lazy window resets — only persist when something actually changed.
        var limits = GetTierLimits(counter.Tier);
        var changed = false;

        if (now >= counter.FlashWindowResetAt)
        {
            counter.FlashRequestsUsed = 0;
            counter.FlashWindowResetAt = now.AddDays(limits.FlashWindowDays);
            changed = true;
        }
        if (now >= counter.ProWindowResetAt)
        {
            counter.ProRequestsUsed = 0;
            counter.ProWindowResetAt = now.AddDays(limits.ProWindowDays);
            changed = true;
        }
        if (now >= counter.AgentWindowResetAt)
        {
            counter.AgentTokensUsed = 0;
            counter.AgentWindowResetAt = now.AddDays(limits.AgentWindowDays);
            changed = true;
        }

        if (changed)
            await _repository.UpsertAsync(counter, ct);

        return counter;
    }

    private static UserUsageCounterEntity NewCounter(Guid userId, DateTime now)
    {
        return new UserUsageCounterEntity
        {
            UserId = userId,
            Tier = SubscriptionTier.Free,
            FlashRequestsUsed = 0,
            ProRequestsUsed = 0,
            AgentTokensUsed = 0,
            FlashWindowResetAt = now.AddDays(7),
            ProWindowResetAt = now.AddDays(7),
            AgentWindowResetAt = now.AddDays(30)
        };
    }

    private TierLimits GetTierLimits(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Pro => _options.Value.Pro,
        SubscriptionTier.ProMax => _options.Value.ProMax,
        _ => _options.Value.Free
    };

    // ADMIN_UNLIMITED: добавлено 2026-09-19
    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        return user?.IsAdmin == true;
    }

    private static SubscriptionUsageDto AdminUsage() =>
        new("Admin",
            0, long.MaxValue, DateTime.MaxValue,
            0, long.MaxValue, DateTime.MaxValue,
            0, long.MaxValue, DateTime.MaxValue);
}
