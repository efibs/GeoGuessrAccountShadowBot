using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeoGuessrAccountShadowBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ControlAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscordUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FriendRequestCards",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FriendRequestCards", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    DeliveredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FailedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PartnerThreads",
                columns: table => new
                {
                    PartnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    TitleNick = table.Column<string>(type: "TEXT", nullable: false),
                    TitleCheckedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnerThreads", x => x.PartnerUserId);
                });

            migrationBuilder.CreateTable(
                name: "PendingReplies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<string>(type: "TEXT", nullable: false),
                    TextHash = table.Column<string>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingReplies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerNames",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Nick = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerNames", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedEvents",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedEvents", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "States",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_States", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_DeliveredAt_FailedAt",
                table: "Outbox",
                columns: new[] { "DeliveredAt", "FailedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PartnerThreads_ThreadId",
                table: "PartnerThreads",
                column: "ThreadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingReplies_PeerId_TextHash",
                table: "PendingReplies",
                columns: new[] { "PeerId", "TextHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEvents_ProcessedAt",
                table: "ProcessedEvents",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlAudits");

            migrationBuilder.DropTable(
                name: "FriendRequestCards");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "PartnerThreads");

            migrationBuilder.DropTable(
                name: "PendingReplies");

            migrationBuilder.DropTable(
                name: "PlayerNames");

            migrationBuilder.DropTable(
                name: "ProcessedEvents");

            migrationBuilder.DropTable(
                name: "States");
        }
    }
}
