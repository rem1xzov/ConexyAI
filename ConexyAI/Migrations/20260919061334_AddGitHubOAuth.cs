using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubOAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "conexy_pending_action",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "conexy_chat_message",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    GitHubId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    GitHubUsername = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SubscriptionTier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            // GITHUB_OAUTH: backfill placeholder users so the FK constraints below are
            // satisfied by any pre-existing rows (legacy dev-token data). These rows are
            // unauthenticated orphans — no GitHubId/email — so they can never be logged into,
            // but they keep old data referentially consistent.
            migrationBuilder.Sql("""
                INSERT INTO users ("Id", "Email", "EmailConfirmed", "PasswordHash", "GitHubId", "GitHubUsername", "SubscriptionTier", "CreatedAt", "LastLoginAt")
                SELECT v."Id", NULL, false, NULL, NULL, NULL, 'Free', now(), now()
                FROM (
                    SELECT '00000000-0000-0000-0000-000000000000'::uuid AS "Id"
                    UNION
                    SELECT DISTINCT "UserId" FROM document
                    UNION
                    SELECT DISTINCT "UserId" FROM user_memory_fact
                    UNION
                    SELECT DISTINCT "UserId" FROM user_usage_counter
                ) AS v
                ON CONFLICT ("Id") DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_conexy_pending_action_UserId",
                table: "conexy_pending_action",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_conexy_chat_message_UserId",
                table: "conexy_chat_message",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_GitHubId",
                table: "users",
                column: "GitHubId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_conexy_chat_message_users_UserId",
                table: "conexy_chat_message",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_conexy_pending_action_users_UserId",
                table: "conexy_pending_action",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_document_users_UserId",
                table: "document",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_memory_fact_users_UserId",
                table: "user_memory_fact",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_usage_counter_users_UserId",
                table: "user_usage_counter",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conexy_chat_message_users_UserId",
                table: "conexy_chat_message");

            migrationBuilder.DropForeignKey(
                name: "FK_conexy_pending_action_users_UserId",
                table: "conexy_pending_action");

            migrationBuilder.DropForeignKey(
                name: "FK_document_users_UserId",
                table: "document");

            migrationBuilder.DropForeignKey(
                name: "FK_user_memory_fact_users_UserId",
                table: "user_memory_fact");

            migrationBuilder.DropForeignKey(
                name: "FK_user_usage_counter_users_UserId",
                table: "user_usage_counter");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropIndex(
                name: "IX_conexy_pending_action_UserId",
                table: "conexy_pending_action");

            migrationBuilder.DropIndex(
                name: "IX_conexy_chat_message_UserId",
                table: "conexy_chat_message");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "conexy_pending_action");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "conexy_chat_message");
        }
    }
}
