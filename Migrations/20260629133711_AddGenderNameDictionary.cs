using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompetitionManagementSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddGenderNameDictionary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenderNameDictionary",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_BIN2"),
                    Gender = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenderNameDictionary", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenderNameDictionary_Gender_IsActive",
                table: "GenderNameDictionary",
                columns: new[] { "Gender", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_GenderNameDictionary_NormalizedName",
                table: "GenderNameDictionary",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenderNameDictionary");
        }
    }
}
