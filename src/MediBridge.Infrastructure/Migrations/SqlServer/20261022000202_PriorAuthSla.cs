using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediBridge.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class PriorAuthSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LifecycleJson",
                table: "PriorAuthRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextSlaCheckTicks",
                table: "PriorAuthRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "PriorAuthRequests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "StateJson",
                table: "PriorAuthEvents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriorAuthRequests_Outcome_NextSlaCheckTicks",
                table: "PriorAuthRequests",
                columns: new[] { "Outcome", "NextSlaCheckTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriorAuthRequests_Outcome_NextSlaCheckTicks",
                table: "PriorAuthRequests");

            migrationBuilder.DropColumn(
                name: "LifecycleJson",
                table: "PriorAuthRequests");

            migrationBuilder.DropColumn(
                name: "NextSlaCheckTicks",
                table: "PriorAuthRequests");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "PriorAuthRequests");

            migrationBuilder.DropColumn(
                name: "StateJson",
                table: "PriorAuthEvents");
        }
    }
}
