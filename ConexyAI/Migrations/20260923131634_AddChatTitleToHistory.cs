using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddChatTitleToHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "conexy_chat_message",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "conexy_chat_message");
        }
    }
}
