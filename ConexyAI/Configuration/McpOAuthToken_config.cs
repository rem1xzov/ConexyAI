using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConexyAI.Configuration;

// MCP_OAUTH: добавлено 2026-11-03 — одна строка на пару (пользователь, MCP-сервер).
// Уникальный индекс защищает от гонки двух параллельных авторизаций одного сервера.
public class McpOAuthToken_config : IEntityTypeConfiguration<McpOAuthTokenEntity>
{
    public void Configure(EntityTypeBuilder<McpOAuthTokenEntity> builder)
    {
        builder.ToTable("mcp_oauth_tokens");

        builder.HasKey(x => new { x.UserId, x.ServerKey });
        builder.HasIndex(x => new { x.UserId, x.ServerKey }).IsUnique();

        builder.Property(x => x.ServerKey).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Tokens).HasColumnType("text").IsRequired(false);
        builder.Property(x => x.ClientRegistration).HasColumnType("text").IsRequired(false);
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
