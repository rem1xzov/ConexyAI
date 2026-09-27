using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddCoworkTokenBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // COWORK_BUDGET: у Cowork свой счётчик токенов, отдельный от агентского.
            migrationBuilder.AddColumn<long>(
                name: "CoworkTokensUsed",
                table: "user_usage_counter",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Дефолт 0001-01-01 не случаен: окно сбрасывается лениво по условию "now >= ResetAt",
            // поэтому у всех существующих строк счётчик обнулится и окно откроется заново
            // при первом же обращении к лимитам — отдельный апдейт данных не нужен.
            migrationBuilder.AddColumn<DateTime>(
                name: "CoworkWindowResetAt",
                table: "user_usage_counter",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoworkTokensUsed",
                table: "user_usage_counter");

            migrationBuilder.DropColumn(
                name: "CoworkWindowResetAt",
                table: "user_usage_counter");
        }
    }
}
