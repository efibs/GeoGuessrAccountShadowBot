// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using AwesomeAssertions;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Model;
using GeoGuessrAccountShadowBot.Infrastructure.Persistence;
using GeoGuessrAccountShadowBot.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GeoGuessrAccountShadowBot.Tests.Infrastructure;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Migrations_MatchTheModel()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var scope = host.Services.CreateAsyncScope();
        await using (scope)
        {
            scope.ServiceProvider.GetRequiredService<ShadowDbContext>().Database.HasPendingModelChanges()
                .Should().BeFalse("a model change needs a migration");
        }
    }

    [Fact]
    public async Task Uncommitted_WorkIsLost()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        await host.WithStoreAsync(store =>
        {
            store.Enqueue(new StatusMessage("t", "d", false));
            return store.TryMarkProcessedAsync("key");
        });

        (await host.ReadOutboxAsync()).Should().BeEmpty();
        (await host.WithStoreAsync(store => store.FindProcessedAsync(["key"]))).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessedKey_IsOnlyNewOnce()
    {
        await using var host = await ShadowTestHost.CreateAsync();

        (await host.WithStoreAsync(async store =>
        {
            var first = await store.TryMarkProcessedAsync("key");
            await store.CommitAsync();
            return first;
        })).Should().BeTrue();

        (await host.WithStoreAsync(store => store.TryMarkProcessedAsync("key"))).Should().BeFalse();
    }

    [Fact]
    public async Task FriendshipSnapshot_RoundTrips()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var snapshot = new FriendshipSet(["a", "b"], ["c"], ImmutableSortedSet<string>.Empty);

        await host.WithStoreAsync(async store =>
        {
            await store.WriteStateAsync("friendships", snapshot);
            await store.CommitAsync();
            return true;
        });

        var read = await host.WithStoreAsync(store => store.ReadStateAsync<FriendshipSet>("friendships"));
        read!.Friends.Should().Equal("a", "b");
        read.Incoming.Should().Equal("c");
        read.Outgoing.Should().BeEmpty();
    }

    [Fact]
    public async Task ClubRoster_RoundTrips()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var roster = ClubRoster.From([new ClubMember("a", "nick", 2)]);

        await host.WithStoreAsync(async store =>
        {
            await store.WriteStateAsync("club-roster", roster);
            await store.CommitAsync();
            return true;
        });

        var read = await host.WithStoreAsync(store => store.ReadStateAsync<ClubRoster>("club-roster"));
        read!.Members["a"].Should().Be(new ClubMember("a", "nick", 2));
    }

    [Fact]
    public async Task PartnerThread_CanBeRepointedAfterItsPostIsDeleted()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var thread = new PartnerThread("peer", 1, "nick", host.Time.GetUtcNow());

        await host.WithStoreAsync(async store =>
        {
            await store.SavePartnerThreadAsync(thread);
            await store.CommitAsync();
            await store.SavePartnerThreadAsync(thread with { ThreadId = 2 });
            await store.CommitAsync();
            return true;
        });

        (await host.WithStoreAsync(store => store.FindPartnerThreadByThreadIdAsync(2)))!.PartnerUserId.Should().Be("peer");
        (await host.WithStoreAsync(store => store.FindPartnerThreadByThreadIdAsync(1))).Should().BeNull();
    }

    [Fact]
    public async Task LargeDiscordIds_RoundTrip()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        const ulong snowflake = 1_300_000_000_000_000_000;

        await host.WithStoreAsync(async store =>
        {
            store.AddPendingReply("peer", "hash", snowflake, snowflake + 1, host.Time.GetUtcNow());
            await store.CommitAsync();
            return true;
        });

        var pending = await host.WithStoreAsync(store => store.FindPendingReplyAsync("peer", "hash"));
        pending!.ThreadId.Should().Be(snowflake);
        pending.MessageId.Should().Be(snowflake + 1);
    }

    [Fact]
    public async Task PendingReplies_AreFoundOldestFirst()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.WithStoreAsync(async store =>
        {
            store.AddPendingReply("peer", "hash", 1, 10, host.Time.GetUtcNow());
            store.AddPendingReply("peer", "hash", 1, 20, host.Time.GetUtcNow());
            await store.CommitAsync();
            return true;
        });

        (await host.WithStoreAsync(store => store.FindPendingReplyAsync("peer", "hash")))!.MessageId.Should().Be(10);
    }

    [Fact]
    public async Task PendingReplies_CompareTimesInTheDatabase()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        var now = host.Time.GetUtcNow();
        await host.WithStoreAsync(async store =>
        {
            store.AddPendingReply("old", "hash", 1, 10, now - TimeSpan.FromMinutes(5));
            store.AddPendingReply("new", "hash", 1, 20, now);
            await store.CommitAsync();
            return true;
        });

        var expired = await host.WithStoreAsync(store => store.ReadPendingRepliesOlderThanAsync(now - TimeSpan.FromMinutes(1)));

        expired.Should().ContainSingle().Which.PeerId.Should().Be("old");
    }

    [Fact]
    public async Task Prune_RemovesOnlyWhatIsOld()
    {
        await using var host = await ShadowTestHost.CreateAsync();
        await host.WithStoreAsync(async store =>
        {
            await store.TryMarkProcessedAsync("old");
            store.Enqueue(new StatusMessage("undelivered", "d", false));
            await store.CommitAsync();
            return true;
        });

        host.Time.Advance(TimeSpan.FromDays(100));
        await host.WithStoreAsync(async store =>
        {
            await store.TryMarkProcessedAsync("new");
            await store.CommitAsync();
            await store.PruneAsync(host.Time.GetUtcNow() - TimeSpan.FromDays(90), host.Time.GetUtcNow() - TimeSpan.FromDays(7));
            return true;
        });

        (await host.WithStoreAsync(store => store.FindProcessedAsync(["old", "new"]))).Should().Equal("new");
        (await host.ReadOutboxAsync()).Should().ContainSingle("nothing undelivered may ever be pruned");
    }

    [Fact]
    public async Task Initializer_CreatesTheFolderAndUsesWriteAheadLogging()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shadow-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(folder, "shadow.db");

        var services = new ServiceCollection()
            .AddDbContext<ShadowDbContext>(options => options.UseSqlite($"Data Source={path}"))
            .BuildServiceProvider();

        try
        {
            await new DatabaseInitializer(services.GetRequiredService<IServiceScopeFactory>()).StartAsync(CancellationToken.None);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode";
            (await command.ExecuteScalarAsync()).Should().Be("wal");
        }
        finally
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task Initializer_SurvivesALeftoverMigrationLock()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shadow-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "shadow.db");
        Directory.CreateDirectory(folder);

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE __EFMigrationsLock (Id INTEGER PRIMARY KEY, Timestamp TEXT); INSERT INTO __EFMigrationsLock VALUES (1, 'stale');";
            await command.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection()
            .AddDbContext<ShadowDbContext>(options => options.UseSqlite($"Data Source={path}"))
            .BuildServiceProvider();

        try
        {
            var start = new DatabaseInitializer(services.GetRequiredService<IServiceScopeFactory>()).StartAsync(CancellationToken.None);
            await start.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
