using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediBridge.Infrastructure.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class PriorAuthIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriorAuthRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    CanonicalJson = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceFormat = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ValidationJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriorAuthRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriorAuthEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "BINARY"),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriorAuthEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriorAuthEvents_PriorAuthRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "PriorAuthRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriorAuthEvents_RequestId_Id",
                table: "PriorAuthEvents",
                columns: new[] { "RequestId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriorAuthEvents");

            migrationBuilder.DropTable(
                name: "PriorAuthRequests");
        }
    }
}
