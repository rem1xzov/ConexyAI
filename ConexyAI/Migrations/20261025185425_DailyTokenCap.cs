using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class DailyTokenCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AgentDailyResetAt",
                table: "user_usage_counter",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "AgentTokensToday",
                table: "user_usage_counter",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "CoworkRequestsUsed",
                table: "user_usage_counter",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentDailyResetAt",
                table: "user_usage_counter");

            migrationBuilder.DropColumn(
                name: "AgentTokensToday",
                table: "user_usage_counter");

            migrationBuilder.DropColumn(
                name: "CoworkRequestsUsed",
                table: "user_usage_counter");
        }
    }
}
