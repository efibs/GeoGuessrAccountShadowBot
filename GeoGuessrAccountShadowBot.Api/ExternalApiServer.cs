// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Api;

/// <summary>
/// Serves this instance to the clients in <c>ExternalApi:Clients</c>. It runs its own small web
/// host, which reads no configuration of its own, so that nothing but <c>ExternalApi</c> decides
/// where it listens. Every request needs a client's token, and each client only gets what it was
/// allowed.
/// </summary>
internal sealed partial class ExternalApiServer(
    IOptions<ShadowOptions> options,
    ExternalApiService api,
    ILoggerFactory loggers,
    TimeProvider timeProvider,
    ILogger<ExternalApiServer> logger) : IHostedService, IAsyncDisposable
{
    private const string JsonType = "application/json";
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefusalLogInterval = TimeSpan.FromSeconds(10);

    private WebApplication? _app;
    private long _lastRefusalLoggedAt = long.MinValue;

    /// <summary>The port it listens on, once started. Tests ask for port 0 and read the real one here.</summary>
    public int? Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.ExternalApi;
        if (!settings.Enabled)
        {
            return;
        }

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 0;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.ListenAnyIP(settings.Port);
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton(loggers);
        builder.Services.AddSingleton(timeProvider);

        // The bot's host owns the process: it handles signals, and stops this one.
        builder.Services.AddSingleton<IHostLifetime, PassiveLifetime>();

        var app = builder.Build();
        Map(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;

        Port = new Uri(app.Urls.First()).Port;
        LogListening(Port.Value, settings.Clients.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            // Stopping first ends the open event streams, which would otherwise hold up the stop.
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    private void Map(WebApplication app)
    {
        var stopping = app.Lifetime.ApplicationStopping;
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/account", (Delegate)((HttpContext context) =>
            Serve(context, ExternalApiData.Account, () => Task.FromResult(api.ReadAccount()))));

        v1.MapGet("/players/{id}", (HttpContext context, string id) =>
            AutomationEngine.ParseUserId(id) is { } userId && userId == id.ToLowerInvariant()
                ? Serve(context, ExternalApiData.Players, () => api.ReadPlayerAsync(userId, context.RequestAborted))
                : Task.FromResult(Authenticate(context, ExternalApiData.Players, out _) ?? Results.BadRequest()));

        v1.MapGet("/lists/{name}", (HttpContext context, string name) =>
            Enum.TryParse<AutomationAudience>(name, ignoreCase: true, out var audience) && Enum.IsDefined(audience) && !name.Any(char.IsDigit)
                ? Serve(context, ExternalApiData.Lists, async () =>
                    new JsonObject { ["ids"] = new JsonArray([.. (await api.ReadAudienceAsync(audience, context.RequestAborted).ConfigureAwait(false)).Select(id => JsonValue.Create(id))]) })
                : Task.FromResult(Authenticate(context, ExternalApiData.Lists, out _) ?? Results.NotFound()));

        v1.MapGet("/events", (HttpContext context) =>
        {
            if (Authenticate(context, null, out var client) is { } refusal)
            {
                return refusal;
            }

            if (client!.Events.Count == 0)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            long? after = long.TryParse(context.Request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out var last) ? last : null;
            LogStreamOpened(client.Name, after);

            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.ServerSentEvents(Stream(client, after, context.RequestAborted, stopping));
        });
    }

    private async IAsyncEnumerable<SseItem<string>> Stream(
        ExternalApiClient client, long? after, [EnumeratorCancellation] CancellationToken aborted, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, stopping);
        var enumerator = api.StreamAsync(client, after, PingInterval, linked.Token).GetAsyncEnumerator(linked.Token);
        try
        {
            while (await NextAsync(enumerator).ConfigureAwait(false))
            {
                yield return ToSse(enumerator.Current);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            LogStreamClosed(client.Name);
        }
    }

    /// <summary>Writes an item as the client reads it. Only events carry an id, which is what a client resumes from.</summary>
    private static SseItem<string> ToSse(SourceStreamItem item) => item switch
    {
        SourceHello hello => new SseItem<string>(new JsonObject { ["oldest"] = hello.Oldest, ["newest"] = hello.Newest }.ToJsonString(), "hello"),
        SourceEvent shadowEvent => new SseItem<string>(Json(shadowEvent), "event") { EventId = shadowEvent.Id.ToString(CultureInfo.InvariantCulture) },
        _ => new SseItem<string>("{}", "ping")
    };

    private static string Json(SourceEvent shadowEvent) => new JsonObject
    {
        ["id"] = shadowEvent.Id,
        ["trigger"] = shadowEvent.Trigger,
        ["player_id"] = shadowEvent.PlayerId,
        ["occurred_at"] = shadowEvent.OccurredAt,
        ["recorded_at"] = shadowEvent.RecordedAt,
        ["data"] = shadowEvent.Data
    }.ToJsonString();

    /// <summary>The end of a stream is the client leaving or the bot stopping, neither of which is an error.</summary>
    private static async Task<bool> NextAsync(IAsyncEnumerator<SourceStreamItem> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<IResult> Serve(HttpContext context, ExternalApiData data, Func<Task<JsonObject>> read)
    {
        if (Authenticate(context, data, out _) is { } refusal)
        {
            return refusal;
        }

        try
        {
            var json = await read().ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(json.ToJsonString(), JsonType);
        }
        catch (ExternalApiNotReadyException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Checks the token, and that its client may read the data.</summary>
    /// <returns>The refusal to answer with, or null if the request may go on.</returns>
    private IResult? Authenticate(HttpContext context, ExternalApiData? data, out ExternalApiClient? client)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;

        client = api.Authenticate(token);
        if (client is null)
        {
            LogRefusalThrottled(context);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (data is { } wanted && !client.May(wanted))
        {
            LogForbidden(client.Name, wanted);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return null;
    }

    /// <summary>Logs refused requests, but at most every few seconds, so a flood can't fill the log.</summary>
    private void LogRefusalThrottled(HttpContext context)
    {
        var now = timeProvider.GetTimestamp();
        var last = Interlocked.Read(ref _lastRefusalLoggedAt);
        if (last != long.MinValue && timeProvider.GetElapsedTime(last, now) < RefusalLogInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastRefusalLoggedAt, now, last) == last)
        {
            LogUnauthorized(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The external API listens on port {Port} for {Clients} clients")]
    private partial void LogListening(int port, int clients);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a request without a valid token from {Address} for {Path}")]
    private partial void LogUnauthorized(string address, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {Client} asked for {Data}, which it may not read")]
    private partial void LogForbidden(string client, ExternalApiData data);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Client} opened the event stream after {After}")]
    private partial void LogStreamOpened(string client, long? after);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Client} closed the event stream")]
    private partial void LogStreamClosed(string client);
}

/// <summary>A lifetime that waits for nothing and handles no signals, for a host inside another.</summary>
internal sealed class PassiveLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
