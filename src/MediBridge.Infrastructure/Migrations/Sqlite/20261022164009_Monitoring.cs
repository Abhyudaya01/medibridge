using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediBridge.Infrastructure.Migrations.Sqlite
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
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CompletedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Route = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    Exception = table.Column<bool>(type: "INTEGER", nullable: false),
                    Aborted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Replayed = table.Column<bool>(type: "INTEGER", nullable: false)
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
            migrationBuilder.Sql("CREATE TRIGGER EndpointObservations_NoUpdate BEFORE UPDATE ON EndpointObservations BEGIN SELECT RAISE(ABORT, 'Append-only audit'); END; CREATE TRIGGER EndpointObservations_NoDelete BEFORE DELETE ON EndpointObservations BEGIN SELECT RAISE(ABORT, 'Append-only audit'); END;");
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
