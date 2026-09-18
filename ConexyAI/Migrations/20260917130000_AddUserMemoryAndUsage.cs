using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMemoryAndUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_memory_fact",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactText = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_memory_fact", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_usage_counter",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FlashRequestsUsed = table.Column<int>(type: "integer", nullable: false),
                    ProRequestsUsed = table.Column<int>(type: "integer", nullable: false),
                    AgentTokensUsed = table.Column<long>(type: "bigint", nullable: false),
                    FlashWindowResetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProWindowResetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AgentWindowResetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_usage_counter", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_memory_fact_UserId",
                table: "user_memory_fact",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_memory_fact");

            migrationBuilder.DropTable(
                name: "user_usage_counter");
        }
    }
}
