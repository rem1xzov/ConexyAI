using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenTopUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CoderTokenTopUp",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CoworkTokenTopUp",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "payments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "subscription");

            migrationBuilder.AddColumn<string>(
                name: "Pool",
                table: "payments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TokenAmount",
                table: "payments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoderTokenTopUp",
                table: "users");

            migrationBuilder.DropColumn(
                name: "CoworkTokenTopUp",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "Pool",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "TokenAmount",
                table: "payments");
        }
    }
}
