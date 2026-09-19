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

    // GITHUB_OAUTH: добавлено 2026-09-19
    public DbSet<User> Users => Set<User>();

    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    public DbSet<PendingActionEntity> PendingActions => Set<PendingActionEntity>();

    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    public DbSet<UserMemoryFactEntity> UserMemoryFacts => Set<UserMemoryFactEntity>();
    public DbSet<UserUsageCounterEntity> UserUsageCounters => Set<UserUsageCounterEntity>();

    // RAG: добавлено 2026-09-17
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    // SUPPORT: добавлено 2026-09-19
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}