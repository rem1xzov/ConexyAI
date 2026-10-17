using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class SupportMultiTicketsAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_support_ticket_UserId",
                table: "support_ticket");

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "support_ticket",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UserLastReadAt",
                table: "support_ticket",
                type: "timestamp with time zone",
                nullable: true);

            // Старые обращения: считаем прочитанными и берём тему из первого сообщения пользователя.
            migrationBuilder.Sql("UPDATE support_ticket SET \"UserLastReadAt\" = \"LastMessageAt\";");
            migrationBuilder.Sql("""
                UPDATE support_ticket t SET "Subject" = left(regexp_replace(m."Content", '\s+', ' ', 'g'), 80)
                FROM (
                    SELECT DISTINCT ON ("TicketId") "TicketId", "Content"
                    FROM support_message
                    WHERE "AuthorType" = 'User' AND length("Content") > 0
                    ORDER BY "TicketId", "CreatedAt"
                ) m
                WHERE m."TicketId" = t."Id" AND t."Subject" IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "support_attachment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_attachment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_support_attachment_support_message_MessageId",
                        column: x => x.MessageId,
                        principalTable: "support_message",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_support_ticket_UserId_LastMessageAt",
                table: "support_ticket",
                columns: new[] { "UserId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_support_attachment_MessageId",
                table: "support_attachment",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "support_attachment");

            migrationBuilder.DropIndex(
                name: "IX_support_ticket_UserId_LastMessageAt",
                table: "support_ticket");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "support_ticket");

            migrationBuilder.DropColumn(
                name: "UserLastReadAt",
                table: "support_ticket");

            migrationBuilder.CreateIndex(
                name: "IX_support_ticket_UserId",
                table: "support_ticket",
                column: "UserId");
        }
    }
}
