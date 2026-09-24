using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class ChatOwnershipPrivacyAndTokenVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TokenVersion",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuppressed",
                table: "user_memory_fact",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceChatId",
                table: "user_memory_fact",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ChatId",
                table: "conexy",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Model = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat", x => x.Id);
                    table.ForeignKey(
                        name: "FK_chat_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_preferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AboutMe = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    ResponseStyle = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    MemoryEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_preferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_user_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conexy_ChatId",
                table: "conexy",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_chat_UserId",
                table: "chat",
                column: "UserId");

            // CHAT_OWNERSHIP: ревью M9 — строки ходов удалённых пользователей (их и чинит этот FK)
            // сначала убираются, иначе внешний ключ не создастся.
            migrationBuilder.Sql(@"DELETE FROM conexy WHERE ""UserId"" NOT IN (SELECT ""Id"" FROM users);");

            migrationBuilder.AddForeignKey(
                name: "FK_conexy_users_UserId",
                table: "conexy",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conexy_users_UserId",
                table: "conexy");

            migrationBuilder.DropTable(
                name: "chat");

            migrationBuilder.DropTable(
                name: "user_preferences");

            migrationBuilder.DropIndex(
                name: "IX_conexy_ChatId",
                table: "conexy");

            migrationBuilder.DropColumn(
                name: "TokenVersion",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsSuppressed",
                table: "user_memory_fact");

            migrationBuilder.DropColumn(
                name: "SourceChatId",
                table: "user_memory_fact");

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "conexy");
        }
    }
}
