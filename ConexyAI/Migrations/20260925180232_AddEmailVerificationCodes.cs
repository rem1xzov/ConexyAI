using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_verification_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttemptsLeft = table.Column<int>(type: "integer", nullable: false),
                    LastSentIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_verification_codes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_verification_codes_Email",
                table: "email_verification_codes",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_email_verification_codes_LastSentIp",
                table: "email_verification_codes",
                column: "LastSentIp");

            // EMAIL_VERIFICATION: every account that exists today was created with
            // EmailConfirmed = false and logs in with email + password. The login path now refuses
            // unconfirmed accounts, so without this single statement the entire existing user base
            // would be locked out. New accounts are created already confirmed, so nothing to do for
            // them.
            migrationBuilder.Sql("UPDATE users SET \"EmailConfirmed\" = true WHERE \"EmailConfirmed\" = false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_verification_codes");
        }
    }
}
