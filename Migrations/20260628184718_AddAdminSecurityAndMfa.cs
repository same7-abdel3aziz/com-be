using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompetitionManagementSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminSecurityAndMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- MFA columns on AspNetUsers ----
            migrationBuilder.AddColumn<string>(
                name: "TotpSecretEncrypted",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TotpEnabled",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TotpRecoveryCodesEncrypted",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TotpEnabledAtUtc",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TotpLastVerifiedAtUtc",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            // ---- AdminAllowedIpRanges ----
            migrationBuilder.CreateTable(
                name: "AdminAllowedIpRanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpRange = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAllowedIpRanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAllowedIpRanges_IpRange",
                table: "AdminAllowedIpRanges",
                column: "IpRange",
                unique: true);

            // ---- AdminSecuritySettings (singleton row, Id always 1) ----
            migrationBuilder.CreateTable(
                name: "AdminSecuritySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    EnableAdminIpRestriction = table.Column<bool>(type: "bit", nullable: false),
                    RequireMfaForAdmins = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminSecuritySettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AdminSecuritySettings",
                columns: new[] { "Id", "EnableAdminIpRestriction", "RequireMfaForAdmins", "UpdatedAtUtc", "UpdatedByUserId" },
                values: new object[] { 1, false, false, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AdminAllowedIpRanges");
            migrationBuilder.DropTable(name: "AdminSecuritySettings");

            migrationBuilder.DropColumn(name: "TotpSecretEncrypted", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "TotpEnabled", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "TotpRecoveryCodesEncrypted", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "TotpEnabledAtUtc", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "TotpLastVerifiedAtUtc", table: "AspNetUsers");
        }
    }
}
