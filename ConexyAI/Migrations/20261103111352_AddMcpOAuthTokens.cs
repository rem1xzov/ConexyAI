using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOAuthTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_oauth_tokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Tokens = table.Column<string>(type: "text", nullable: true),
                    ClientRegistration = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_oauth_tokens", x => new { x.UserId, x.ServerKey });
                    table.ForeignKey(
                        name: "FK_mcp_oauth_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_oauth_tokens_UserId_ServerKey",
                table: "mcp_oauth_tokens",
                columns: new[] { "UserId", "ServerKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_oauth_tokens");
        }
    }
}
