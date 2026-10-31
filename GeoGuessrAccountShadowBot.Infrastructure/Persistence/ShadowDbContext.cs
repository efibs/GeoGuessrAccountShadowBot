// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GeoGuessrAccountShadowBot.Infrastructure.Persistence;

internal sealed class ProcessedEventRow
{
    public required string Key { get; set; }

    public required DateTimeOffset ProcessedAt { get; set; }
}

internal sealed class StateRow
{
    public required string Key { get; set; }

    public required string Json { get; set; }
}

internal sealed class OutboxRow
{
    public long Id { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>The message as JSON. Cleared on delivery, so chat text isn't kept at rest.</summary>
    public string? Payload { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public string? FailureReason { get; set; }
}

internal sealed class PartnerThreadRow
{
    public required string PartnerUserId { get; set; }

    public required ulong ThreadId { get; set; }

    public required string TitleNick { get; set; }

    public required DateTimeOffset TitleCheckedAt { get; set; }
}

internal sealed class FriendRequestCardRow
{
    public required string UserId { get; set; }

    public required ulong ChannelId { get; set; }

    public required ulong MessageId { get; set; }
}

internal sealed class PendingReplyRow
{
    public long Id { get; set; }

    public required string PeerId { get; set; }

    public required string TextHash { get; set; }

    public required ulong ThreadId { get; set; }

    public required ulong MessageId { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}

internal sealed class PlayerNameRow
{
    public required string UserId { get; set; }

    public required string Nick { get; set; }

    public required DateTimeOffset FetchedAt { get; set; }
}

internal sealed class ControlAuditRow
{
    public long Id { get; set; }

    public required DateTimeOffset At { get; set; }

    public required ulong DiscordUserId { get; set; }

    public required string Action { get; set; }

    public required string Target { get; set; }

    public required string Outcome { get; set; }
}

/// <summary>
/// The bot's SQLite database.
/// </summary>
internal sealed class ShadowDbContext(DbContextOptions<ShadowDbContext> options) : DbContext(options)
{
    public DbSet<ProcessedEventRow> ProcessedEvents => Set<ProcessedEventRow>();

    public DbSet<StateRow> States => Set<StateRow>();

    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    public DbSet<PartnerThreadRow> PartnerThreads => Set<PartnerThreadRow>();

    public DbSet<FriendRequestCardRow> FriendRequestCards => Set<FriendRequestCardRow>();

    public DbSet<PendingReplyRow> PendingReplies => Set<PendingReplyRow>();

    public DbSet<PlayerNameRow> PlayerNames => Set<PlayerNameRow>();

    public DbSet<ControlAuditRow> ControlAudits => Set<ControlAuditRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        // SQLite has no date type, and this keeps stored values comparable in queries.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedEventRow>(entity =>
        {
            entity.HasKey(row => row.Key);
            entity.HasIndex(row => row.ProcessedAt);
        });

        modelBuilder.Entity<StateRow>().HasKey(row => row.Key);

        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.DeliveredAt, row.FailedAt });
        });

        modelBuilder.Entity<PartnerThreadRow>(entity =>
        {
            entity.HasKey(row => row.PartnerUserId);
            entity.HasIndex(row => row.ThreadId).IsUnique();
        });

        modelBuilder.Entity<FriendRequestCardRow>().HasKey(row => row.UserId);

        modelBuilder.Entity<PendingReplyRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.PeerId, row.TextHash });
        });

        modelBuilder.Entity<PlayerNameRow>().HasKey(row => row.UserId);
        modelBuilder.Entity<ControlAuditRow>().HasKey(row => row.Id);
    }
}

/// <summary>
/// Lets <c>dotnet ef</c> create the context without starting the bot.
/// </summary>
internal sealed class ShadowDbContextFactory : IDesignTimeDbContextFactory<ShadowDbContext>
{
    public ShadowDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ShadowDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
