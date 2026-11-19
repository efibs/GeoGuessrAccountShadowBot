using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoGuessrAccountShadowBot.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class ExternalEventsTable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ExternalEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Trigger = table.Column<string>(type: "TEXT", nullable: false),
                SubjectUserId = table.Column<string>(type: "TEXT", nullable: true),
                OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                RecordedAt = table.Column<long>(type: "INTEGER", nullable: false),
                DataJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExternalEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ExternalEvents_RecordedAt",
            table: "ExternalEvents",
            column: "RecordedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ExternalEvents");
    }
}
