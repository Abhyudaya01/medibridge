using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediBridge.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class Monitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EndpointObservations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CompletedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Route = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    Exception = table.Column<bool>(type: "bit", nullable: false),
                    Aborted = table.Column<bool>(type: "bit", nullable: false),
                    Replayed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuleEvaluations_ReceivedAtTicks_Id",
                table: "RuleEvaluations",
                columns: new[] { "ReceivedAtTicks", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointObservations_CompletedAtTicks_Method_Route",
                table: "EndpointObservations",
                columns: new[] { "CompletedAtTicks", "Method", "Route" });
            migrationBuilder.Sql("CREATE TRIGGER EndpointObservations_AppendOnly ON EndpointObservations INSTEAD OF UPDATE, DELETE AS BEGIN THROW 51000, 'Append-only audit', 1; END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndpointObservations");

            migrationBuilder.DropIndex(
                name: "IX_RuleEvaluations_ReceivedAtTicks_Id",
                table: "RuleEvaluations");
        }
    }
}
