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

    private readonly SqliteConnection _connection;
    private ServiceProvider _services = null!;

    private ShadowTestHost(SqliteConnection connection, ShadowOptions options)
    {
        _connection = connection;
        Options = options;
    }

    public ShadowOptions Options { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    public IGeoGuessrApi Api { get; } = Substitute.For<IGeoGuessrApi>();

    public IGeoGuessrChatSender Sender { get; } = Substitute.For<IGeoGuessrChatSender>();

    public IGuildRoleReader Roles { get; } = Substitute.For<IGuildRoleReader>();

    public RecordingPublisher Publisher { get; } = new();

    public IServiceProvider Services => _services;

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public static async Task<ShadowTestHost> CreateAsync(Action<ShadowOptions>? configure = null)
    {
        var options = TestOptions.Valid();
        configure?.Invoke(options);

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var host = new ShadowTestHost(connection, options);
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
        services.AddSingleton<IDiscordPublisher>(Publisher);
        services.AddSingleton<IDiscordReadiness>(Substitute.For<IDiscordReadiness>());
        services.AddDbContext<ShadowDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IShadowStore, EfShadowStore>();

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

    public Task DrainOutboxAsync() =>
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
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// Records deliveries, and can be told to fail.
/// </summary>
internal sealed class RecordingPublisher : IDiscordPublisher
{
    public List<OutboxMessage> Delivered { get; } = [];

    public Func<OutboxMessage, DeliveryResult?> Failure { get; set; } = _ => null;

    public Task<DeliveryResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        if (Failure(message) is { } failure)
        {
            return Task.FromResult(failure);
        }

        Delivered.Add(message);
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
