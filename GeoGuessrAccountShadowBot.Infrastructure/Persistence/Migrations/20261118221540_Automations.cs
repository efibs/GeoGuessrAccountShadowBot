using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoGuessrAccountShadowBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Automations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutomationRule",
                table: "PendingReplies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationPlayerRuns",
                columns: table => new
                {
                    RuleName = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectUserId = table.Column<string>(type: "TEXT", nullable: false),
                    LastRunAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationPlayerRuns", x => new { x.RuleName, x.SubjectUserId });
                });

            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleName = table.Column<string>(type: "TEXT", nullable: false),
                    RuleJson = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    GroupKey = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectUserId = table.Column<string>(type: "TEXT", nullable: true),
                    EventJson = table.Column<string>(type: "TEXT", nullable: false),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    Manual = table.Column<bool>(type: "INTEGER", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<ulong>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgramCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ResumeAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AdmittedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: true),
                    LogJson = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovalChannelId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ApprovalMessageId = table.Column<ulong>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_FinishedAt_ResumeAt",
                table: "AutomationRuns",
                columns: new[] { "FinishedAt", "ResumeAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_GroupKey",
                table: "AutomationRuns",
                column: "GroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_RuleName_AdmittedAt",
                table: "AutomationRuns",
                columns: new[] { "RuleName", "AdmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationPlayerRuns");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropTable(
                name: "AutomationRuns");

            migrationBuilder.DropColumn(
                name: "AutomationRule",
                table: "PendingReplies");
        }
    }
}
