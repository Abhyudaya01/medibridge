using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediBridge.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class LegacyClaimsBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Claims",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProviderNpi = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MemberId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ProcedureCodes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Amount = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FailureCategory = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RuleVersion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ValidationFailures = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DuplicateKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClaimEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClaimId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimEvents_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimEvents_ClaimId_Id",
                table: "ClaimEvents",
                columns: new[] { "ClaimId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_DuplicateKey",
                table: "Claims",
                column: "DuplicateKey",
                unique: true,
                filter: "[DuplicateKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimEvents");

            migrationBuilder.DropTable(
                name: "Claims");
        }
    }
}
