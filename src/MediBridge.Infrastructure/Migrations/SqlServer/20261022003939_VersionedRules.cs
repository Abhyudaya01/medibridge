using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediBridge.Infrastructure.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class VersionedRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlanType",
                table: "Claims",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.CreateTable(
                name: "RuleEvaluations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TransactionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Domain = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReceivedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CodesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PlanType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LiveVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LiveHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LiveJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LiveOutcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CandidateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CandidateHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CandidateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CandidateOutcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ProtocolAccepted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleEvaluations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleSets",
                columns: table => new
                {
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EffectiveFromTicks = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveUntilTicks = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleSets", x => x.Version);
                });

            migrationBuilder.CreateTable(
                name: "RuleActivations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActivatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleActivations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleActivations_RuleSets_Version",
                        column: x => x.Version,
                        principalTable: "RuleSets",
                        principalColumn: "Version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuleControls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShadowVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleControls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleControls_RuleSets_ShadowVersion",
                        column: x => x.ShadowVersion,
                        principalTable: "RuleSets",
                        principalColumn: "Version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuleActivations_Version",
                table: "RuleActivations",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_RuleControls_ShadowVersion",
                table: "RuleControls",
                column: "ShadowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_RuleEvaluations_CandidateVersion_ReceivedAtTicks",
                table: "RuleEvaluations",
                columns: new[] { "CandidateVersion", "ReceivedAtTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuleActivations");

            migrationBuilder.DropTable(
                name: "RuleControls");

            migrationBuilder.DropTable(
                name: "RuleEvaluations");

            migrationBuilder.DropTable(
                name: "RuleSets");

            migrationBuilder.DropColumn(
                name: "PlanType",
                table: "Claims");
        }
    }
}
