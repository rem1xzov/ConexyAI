using System.Reflection;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.DbContext;

public class DbConexy : Microsoft.EntityFrameworkCore.DbContext
{
    public DbConexy(DbContextOptions<DbConexy> options) : base(options)
    {
    }

    public DbSet<ConexyEntity> Conexy => Set<ConexyEntity>();

    public DbSet<ConexyChatMessageEntity> ChatMessages => Set<ConexyChatMessageEntity>();

    // CHAT_OWNERSHIP: добавлено 2026-09-24 — владелец каждого chat id (ревью C1).
    public DbSet<ChatEntity> Chats => Set<ChatEntity>();

    // GITHUB_OAUTH: добавлено 2026-09-19
    public DbSet<User> Users => Set<User>();

    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    public DbSet<PendingActionEntity> PendingActions => Set<PendingActionEntity>();

    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    public DbSet<UserMemoryFactEntity> UserMemoryFacts => Set<UserMemoryFactEntity>();
    public DbSet<UserUsageCounterEntity> UserUsageCounters => Set<UserUsageCounterEntity>();

    // USER_PREFERENCES: добавлено 2026-09-24
    public DbSet<UserPreferencesEntity> UserPreferences => Set<UserPreferencesEntity>();

    // USER_INTEGRATIONS: добавлено 2026-10-10 — серверное хранение кредов агента (GitHub/MCP).
    public DbSet<UserIntegrationsEntity> UserIntegrations => Set<UserIntegrationsEntity>();

    // RAG: добавлено 2026-09-17
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    // SUPPORT: добавлено 2026-09-19
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    // SUPPORT_ATTACHMENTS: добавлено 2026-10-18
    public DbSet<SupportAttachment> SupportAttachments => Set<SupportAttachment>();

    // AGENT_SKILLS: добавлено 2026-10-18 — скиллы агента пользователя (SKILL.md).
    public DbSet<UserSkill> UserSkills => Set<UserSkill>();

    // EMAIL_VERIFICATION: добавлено 2026-09-24 — неподтверждённые регистрации (код + хэш пароля).
    public DbSet<EmailVerificationCodeEntity> EmailVerificationCodes => Set<EmailVerificationCodeEntity>();

    // YOOKASSA: добавлено 2026-09-27 — платежи за тарифы.
    public DbSet<PaymentEntity> Payments => Set<PaymentEntity>();

    // DESKTOP_AGENT: добавлено 2026-10-15 — ключи десктопного клиента.
    public DbSet<DeviceApiKeyEntity> DeviceApiKeys => Set<DeviceApiKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}