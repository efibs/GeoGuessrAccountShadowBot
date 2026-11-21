// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace GeoGuessrAccountShadowBot.Tests.TestSupport;

/// <summary>
/// The real application layer on a real SQLite database with real migrations, with only the
/// outside world replaced. Keeping the connection open keeps the in-memory database alive.
/// </summary>
internal sealed class ShadowTestHost : IAsyncDisposable
{
    public const string OwnUserId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    public const string PeerId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    public const ulong AuthorizedRole = 500;
    public const ulong AuthorizedUser = 600;

    private readonly SqliteConnection? _connection;
    private readonly string? _databaseFile;
    private readonly IConfiguration? _configuration;
    private readonly bool _lookUpNames;
    private ServiceProvider _services = null!;

    private ShadowTestHost(SqliteConnection? connection, string? databaseFile, bool lookUpNames, ShadowOptions options, IConfiguration? configuration)
    {
        _connection = connection;
        _databaseFile = databaseFile;
        _lookUpNames = lookUpNames;
        _configuration = configuration;
        Options = options;
    }

    /// <summary>Makes commits fail on demand, for testing what happens after one.</summary>
    public FailingCommits Commits { get; } = new();

    public ShadowOptions Options { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    public IGeoGuessrApi Api { get; } = Substitute.For<IGeoGuessrApi>();

    public IGeoGuessrChatSender Sender { get; } = Substitute.For<IGeoGuessrChatSender>();

    public IGuildRoleReader Roles { get; } = Substitute.For<IGuildRoleReader>();

    public RecordingPublisher Publisher { get; } = new();

    public FakeSources Sources { get; } = new();

    public IServiceProvider Services => _services;

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <param name="configure">Changes to the valid test options.</param>
    /// <param name="configuration">Raw configuration, for what isn't bound to options, such as automation rules.</param>
    /// <param name="fileDatabase">
    /// A database file with a connection per unit of work, as in production, instead of one shared
    /// in-memory connection. Only that lets several workers really run at the same time.
    /// </param>
    /// <param name="lookUpNames">
    /// Deliveries look up the player's name in their own unit of work first, as the real Discord
    /// publisher does. Off by default, so other tests can count name lookups.
    /// </param>
    public static async Task<ShadowTestHost> CreateAsync(
        Action<ShadowOptions>? configure = null, IConfiguration? configuration = null, bool fileDatabase = false, bool lookUpNames = false)
    {
        var options = TestOptions.Valid();
        configure?.Invoke(options);

        SqliteConnection? connection = null;
        string? databaseFile = null;
        if (fileDatabase)
        {
            databaseFile = Path.Combine(Path.GetTempPath(), $"shadow-test-{Guid.NewGuid():N}.db");
        }
        else
        {
            connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
        }

        var host = new ShadowTestHost(connection, databaseFile, lookUpNames, options, configuration);
        host._services = host.Build(new ServiceCollection());

        var scope = host._services.CreateAsyncScope();
        await using (scope)
        {
            await scope.ServiceProvider.GetRequiredService<ShadowDbContext>().Database.MigrateAsync();
        }

        host.Get<AccountContext>().Set(OwnUserId, null);
        host.Get<ConnectionStatus>().Update(new ConnectionState(ConnectionPhase.Connected));
        return host;
    }

    private ServiceProvider Build(ServiceCollection services)
    {
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptions<ShadowOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddShadowApplication();

        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Api);
        services.AddSingleton(Sender);
        services.AddSingleton(Roles);
        services.AddSingleton<IExternalSourceFactory>(Sources);
        if (_lookUpNames)
        {
            services.AddScoped<IDiscordPublisher>(provider => new NameResolvingPublisher(provider.GetRequiredService<PlayerDirectory>(), Publisher));
        }
        else
        {
            services.AddSingleton<IDiscordPublisher>(Publisher);
        }

        services.AddSingleton<IDiscordReadiness>(Substitute.For<IDiscordReadiness>());
        services.AddDbContext<ShadowDbContext>(options =>
        {
            if (_connection is not null)
            {
                options.UseSqlite(_connection);
            }
            else
            {
                options.UseSqlite($"Data Source={_databaseFile};Pooling=False");
            }

            options.AddInterceptors(Commits);
        });
        services.AddScoped<EfShadowStore>();
        services.AddScoped<IShadowStore>(provider => provider.GetRequiredService<EfShadowStore>());
        services.AddScoped<IAutomationStore>(provider => provider.GetRequiredService<EfShadowStore>());
        services.AddScoped<IExternalEventStore>(provider => provider.GetRequiredService<EfShadowStore>());
        if (_configuration is not null)
        {
            services.AddSingleton(_configuration);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>Runs an event through the processor, like the pipeline worker does.</summary>
    public Task ProcessAsync(ShadowEvent shadowEvent) =>
        Get<EventProcessor>().ProcessAsync(shadowEvent, CancellationToken.None);

    /// <summary>Processes everything that was published to the queue.</summary>
    public async Task ProcessQueuedAsync()
    {
        var bus = Get<EventBus>();
        while (bus.Reader.TryRead(out var shadowEvent))
        {
            await ProcessAsync(shadowEvent);
        }
    }

    public Task<TimeSpan?> DrainOutboxAsync() =>
        _services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<OutboxDispatcher>().Single().DrainAsync(0, CancellationToken.None);

    /// <summary>Runs code against a fresh store, as a separate unit of work.</summary>
    public async Task<T> WithStoreAsync<T>(Func<IShadowStore, Task<T>> action)
    {
        var scope = _services.CreateAsyncScope();
        await using (scope)
        {
            return await action(scope.ServiceProvider.GetRequiredService<IShadowStore>());
        }
    }

    public Task<IReadOnlyList<OutboxEntry>> ReadOutboxAsync() => WithStoreAsync(store => store.ReadOutboxAsync(1000));

    public async Task<IReadOnlyList<OutboxMessage>> ReadQueuedMessagesAsync() =>
        (await ReadOutboxAsync()).Select(entry => entry.Message).ToArray();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_databaseFile is not null)
        {
            File.Delete(_databaseFile);
        }
    }
}

/// <summary>
/// Fails the next commits it's told to, the way a constraint violation fails a real one.
/// </summary>
internal sealed class FailingCommits : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    private int _toFail;

    public int Failed => _failed;

    private int _failed;

    public void FailNext(int count) => Interlocked.Exchange(ref _toFail, count);

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref _toFail) >= 0)
        {
            Interlocked.Increment(ref _failed);
            throw new DbUpdateException("A commit the test asked to fail.");
        }

        Interlocked.Exchange(ref _toFail, 0);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// Looks up the name of the player a message is about before recording it, in the delivery's own
/// unit of work, like <c>DiscordPublisher</c>. That lookup is what raced in issue #19.
/// </summary>
internal sealed class NameResolvingPublisher(PlayerDirectory players, RecordingPublisher recorder) : IDiscordPublisher
{
    private static readonly TimeSpan SendLatency = TimeSpan.FromMilliseconds(300);

    public async Task<DeliveryResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var userId = message switch
        {
            EventCard card => card.ActorUserId,
            ChatLine line => line.PartnerUserId,
            GeoGuessrAccountShadowBot.Application.Automation.AutomationReport report => report.SubjectUserId,
            _ => null
        };

        if (userId is not null)
        {
            await players.GetNickAsync(userId, cancellationToken);
        }

        // A send to Discord takes a round trip, and the delivery is only recorded after it. That
        // gap is what let the other unit of work commit the same name first.
        await Task.Delay(SendLatency, cancellationToken);
        return await recorder.PublishAsync(message, cancellationToken);
    }
}

/// <summary>
/// Records deliveries, and can be told to fail.
/// </summary>
internal sealed class RecordingPublisher : IDiscordPublisher
{
    public List<OutboxMessage> Delivered { get; } = [];

    public Func<OutboxMessage, DeliveryResult?> Failure { get; set; } = _ => null;

    /// <summary>A copy of what was delivered, safe to read while workers are delivering.</summary>
    public OutboxMessage[] Snapshot()
    {
        lock (Delivered)
        {
            return [.. Delivered];
        }
    }

    public Task<DeliveryResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        if (Failure(message) is { } failure)
        {
            return Task.FromResult(failure);
        }

        lock (Delivered)
        {
            Delivered.Add(message);
        }

        return Task.FromResult(DeliveryResult.Delivered);
    }
}

/// <summary>
/// A valid configuration, starting from the shipped defaults.
/// </summary>
internal static class TestOptions
{
    public static ShadowOptions Valid()
    {
        var options = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.reference.json"))
            .Build()
            .Get<ShadowOptions>()!;

        options.GeoGuessr.NcfaToken = "test-token";
        options.Discord.Token = "test-discord-token";
        options.Discord.GuildId = 1;
        options.Discord.DefaultChannelId = 2;
        options.Forum.ChannelId = 3;
        options.Control.AuthorizedRoleIds = [ShadowTestHost.AuthorizedRole];
        return options;
    }
}
