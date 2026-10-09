using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class SupportBotEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SUPPORT_BOT: AuthorType сначала добавляем, переносим из IsFromAdmin, и только потом
            // удаляем IsFromAdmin (EF сгенерировал drop первым — так данные потерялись бы).
            migrationBuilder.AddColumn<string>(
                name: "AuthorType",
                table: "support_message",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "User");

            migrationBuilder.Sql("UPDATE support_message SET \"AuthorType\" = 'Admin' WHERE \"IsFromAdmin\" = true;");

            migrationBuilder.DropColumn(
                name: "IsFromAdmin",
                table: "support_message");

            migrationBuilder.AddColumn<bool>(
                name: "AdminActive",
                table: "support_ticket",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BotActive",
                table: "support_ticket",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "support_ticket",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedAt",
                table: "support_ticket",
                type: "timestamp with time zone",
                nullable: true);

            // SUPPORT_BOT: старый статус Open переезжает в BotHandling (иначе enum не прочитает строку).
            migrationBuilder.Sql("UPDATE support_ticket SET \"Status\" = 'BotHandling' WHERE \"Status\" = 'Open';");
            migrationBuilder.Sql("UPDATE support_ticket SET \"BotActive\" = false WHERE \"Status\" = 'Closed';");

            migrationBuilder.AlterColumn<Guid>(
                name: "SenderId",
                table: "support_message",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminActive",
                table: "support_ticket");

            migrationBuilder.DropColumn(
                name: "BotActive",
                table: "support_ticket");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "support_ticket");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                table: "support_ticket");

            migrationBuilder.DropColumn(
                name: "AuthorType",
                table: "support_message");

            migrationBuilder.AlterColumn<Guid>(
                name: "SenderId",
                table: "support_message",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFromAdmin",
                table: "support_message",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
