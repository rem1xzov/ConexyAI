using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexyAI.Migrations
{
    /// <inheritdoc />
    public partial class AddPurposeToEmailCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PASSWORD_RESET: строки сброса пароля не несут хэша пароля — он задаётся уже после кода.
            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "email_verification_codes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            // PASSWORD_RESET: до этой миграции в таблице лежали исключительно незавершённые
            // регистрации, поэтому дефолт именно 'signup'. Со сгенерированным EF "" уже начатые
            // регистрации стали бы невидимы для поиска по purpose, и их авторам пришлось бы
            // заполнять форму заново.
            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "email_verification_codes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "signup");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "email_verification_codes");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "email_verification_codes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
