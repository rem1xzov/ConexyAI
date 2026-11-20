using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddUiPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // UI_PREFERENCES: интерфейсные переключатели «Персонализации». Все три по умолчанию
            // выключены, поэтому существующие пользователи не замечают эффекта до включения.
            migrationBuilder.AddColumn<bool>(
                name: "CursorTrail",
                table: "user_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HideGetApp",
                table: "user_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HideUsageRing",
                table: "user_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CursorTrail", table: "user_preferences");
            migrationBuilder.DropColumn(name: "HideGetApp", table: "user_preferences");
            migrationBuilder.DropColumn(name: "HideUsageRing", table: "user_preferences");
        }
    }
}
