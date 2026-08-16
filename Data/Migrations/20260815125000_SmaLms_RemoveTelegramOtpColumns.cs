using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Library_Management_system.Data.Migrations
{
    /// <summary>
    /// Drops the five columns left behind by the Telegram OTP flow.
    /// </summary>
    /// <remarks>
    /// ResetPasswordToken/Expiry stored a six-digit OTP; the three Telegram columns cached the
    /// chat a code was delivered to. Password reset now uses ASP.NET Identity's own token, which
    /// is derived from the security stamp and never persisted, so nothing reads these.
    ///
    /// EF flags this as possible data loss, which is correct in general. Checked before applying:
    /// all five were NULL for every row, so this drops empty columns rather than real data. Down()
    /// restores the shape but cannot restore contents — that is inherent to a column drop and
    /// harmless here.
    /// </remarks>
    public partial class SmaLms_RemoveTelegramOtpColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResetPasswordToken",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ResetPasswordTokenExpiry",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TelegramChatId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TelegramLinkedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TelegramLinkedPhone",
                table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResetPasswordToken",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResetPasswordTokenExpiry",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelegramChatId",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TelegramLinkedAtUtc",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelegramLinkedPhone",
                table: "AspNetUsers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }
    }
}
