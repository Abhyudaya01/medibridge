using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediBridge.Infrastructure.Migrations.Sqlite
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
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unspecified");

            migrationBuilder.CreateTable(
                name: "RuleEvaluations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TransactionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReceivedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CodesJson = table.Column<string>(type: "TEXT", nullable: false),
                    PlanType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LiveVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, collation: "BINARY"),
                    LiveHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LiveJson = table.Column<string>(type: "TEXT", nullable: false),
                    LiveOutcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CandidateVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, collation: "BINARY"),
                    CandidateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CandidateJson = table.Column<string>(type: "TEXT", nullable: true),
                    CandidateOutcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ProtocolAccepted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleEvaluations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleSets",
                columns: table => new
                {
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, collation: "BINARY"),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EffectiveFromTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveUntilTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleSets", x => x.Version);
                });

            migrationBuilder.CreateTable(
                name: "RuleActivations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, collation: "BINARY"),
                    ActivatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShadowVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, collation: "BINARY"),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
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
