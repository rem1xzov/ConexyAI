using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenTopUpUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CoderTopUpUsed",
                table: "user_usage_counter",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CoworkTopUpUsed",
                table: "user_usage_counter",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoderTopUpUsed",
                table: "user_usage_counter");

            migrationBuilder.DropColumn(
                name: "CoworkTopUpUsed",
                table: "user_usage_counter");
        }
    }
}
