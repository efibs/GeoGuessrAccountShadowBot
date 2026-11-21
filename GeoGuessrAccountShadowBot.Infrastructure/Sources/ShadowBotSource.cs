// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Sources;
using Microsoft.Extensions.Logging;

namespace GeoGuessrAccountShadowBot.Infrastructure.Sources;

/// <summary>Makes clients for sources, by their type.</summary>
internal sealed class ExternalSourceFactory(IHttpClientFactory http, ILoggerFactory loggers) : IExternalSourceFactory
{
    public IExternalSource Create(string name, SourceOptions options) =>
        new ShadowBotSource(name, options, http, loggers.CreateLogger<ShadowBotSource>());
}

/// <summary>
/// Another instance of the bot, read through its external API. Requests carry the token as a
/// bearer token, never follow a redirect, and read only small answers.
/// </summary>
internal sealed partial class ShadowBotSource : IExternalSource
{
    public const string DataClient = "external-source";
    public const string StreamClient = "external-source-stream";

    /// <summary>The source pings every 15 seconds, so this much silence means the connection is dead.</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(45);

    private static readonly IReadOnlySet<string> Players = new HashSet<string>(StringComparer.Ordinal) { ExternalSourceNames.PlayerSection };

    private readonly SourceOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ShadowBotSource> _logger;
    private readonly Uri _baseUrl;

    public ShadowBotSource(string name, SourceOptions options, IHttpClientFactory http, ILogger<ShadowBotSource> logger)
    {
        Name = name;
        _options = options;
        _http = http;
        _logger = logger;

        var url = options.Url ?? throw new InvalidOperationException($"Sources:{name}:Url is missing.");
        _baseUrl = url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
    }

    public string Name { get; }

    public IReadOnlySet<string> Sections => ExternalSourceNames.ShadowBotSections;

    public IReadOnlySet<string> PlayerSections => Players;

    public bool HasEvents => true;

    public async Task<JsonObject> ReadSectionAsync(string section, string? playerId, CancellationToken cancellationToken = default)
    {
        var path = section switch
        {
            ExternalSourceNames.PlayerSection when AutomationEngine.ParseUserId(playerId) is { } id => $"api/v1/players/{id}",
            ExternalSourceNames.PlayerSection => throw new ArgumentException("A player section needs a player id.", nameof(playerId)),
            ExternalSourceNames.AccountSection => "api/v1/account",
            _ => throw new ArgumentException($"A shadow bot has no section {section}.", nameof(section))
        };

        return await GetJsonAsync(path, section == ExternalSourceNames.PlayerSection ? "Players" : "Account", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReadAudienceAsync(AutomationAudience audience, CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync($"api/v1/lists/{audience.ToString().ToLowerInvariant()}", "Lists", cancellationToken).ConfigureAwait(false);
        if (json["ids"] is not JsonArray ids)
        {
            throw new ExternalSourceUnavailableException("It answered without a list of ids.");
        }

        return ids.Select(id => id?.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : null).OfType<string>().ToArray();
    }

    public async IAsyncEnumerable<SourceStreamItem> StreamEventsAsync(long? after, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);

        using var request = Request(HttpMethod.Get, "api/v1/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (after is { } last)
        {
            request.Headers.Add("Last-Event-ID", last.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await SendAsync(_http.CreateClient(StreamClient), request, HttpCompletionOption.ResponseHeadersRead, "Events", idle.Token, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var items = SseParser.Create(body).EnumerateAsync(idle.Token).GetAsyncEnumerator(idle.Token);
            try
            {
                while (await NextAsync(items, cancellationToken).ConfigureAwait(false))
                {
                    idle.CancelAfter(IdleTimeout);
                    if (Parse(items.Current) is { } item)
                    {
                        yield return item;
                    }
                }
            }
            finally
            {
                await items.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> NextAsync(IAsyncEnumerator<SseItem<string>> items, CancellationToken cancellationToken)
    {
        try
        {
            return await items.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalSourceUnavailableException($"It sent nothing for {IdleTimeout.TotalSeconds:0} s, so the connection counts as dead.", exception);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new ExternalSourceUnavailableException("The connection dropped.", exception);
        }
    }

    private SourceStreamItem? Parse(SseItem<string> item)
    {
        try
        {
            switch (item.EventType)
            {
                case "hello":
                    var hello = JsonNode.Parse(item.Data)!.AsObject();
                    return new SourceHello(hello["oldest"]!.GetValue<long>(), hello["newest"]!.GetValue<long>());
                case "event":
                    var json = JsonNode.Parse(item.Data)!.AsObject();
                    return new SourceEvent(
                        json["id"]!.GetValue<long>(),
                        json["trigger"]!.GetValue<string>(),
                        json["player_id"]?.GetValue<string>(),
                        json["occurred_at"]!.GetValue<DateTimeOffset>(),
                        json["recorded_at"]!.GetValue<DateTimeOffset>(),
                        json["data"] as JsonObject ?? []);
                case "ping":
                    return new SourcePing();
                default:
                    return null;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            // One unreadable event mustn't block the stream, which would send it again forever.
            LogUnreadable(Name, item.EventType);
            return null;
        }
    }

    private async Task<JsonObject> GetJsonAsync(string path, string what, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await SendAsync(_http.CreateClient(DataClient), request, HttpCompletionOption.ResponseContentRead, what, cancellationToken, cancellationToken).ConfigureAwait(false);

        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
            return json ?? throw new ExternalSourceUnavailableException("It answered with nothing.");
        }
        catch (JsonException exception)
        {
            throw new ExternalSourceUnavailableException("It answered with something that isn't JSON.", exception);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        return request;
    }

    /// <summary>Sends, and turns every failure into one of the two kinds the rules understand.</summary>
    /// <param name="what">What was asked for, as named in <c>ExternalApi:Clients</c> at the source.</param>
    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completion,
        string what,
        CancellationToken requestToken,
        CancellationToken callerToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, completion, requestToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalSourceUnavailableException($"It couldn't be reached ({exception.HttpRequestError}).", exception);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new ExternalSourceUnavailableException("It didn't answer in time.", exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized => new ExternalSourceRefusedException($"It doesn't accept the token in Sources:{Name}:Token."),
            HttpStatusCode.Forbidden when what == "Events" => new ExternalSourceRefusedException(
                "It doesn't send this instance any events. List them in ExternalApi:Clients:<this instance>:Events there."),
            HttpStatusCode.Forbidden => new ExternalSourceRefusedException(
                $"It doesn't let this instance read {what}. Add {what} to ExternalApi:Clients:<this instance>:Data there."),
            HttpStatusCode.NotFound => new ExternalSourceRefusedException($"It answered 404, so Sources:{Name}:Url may not point at a shadow bot with ExternalApi:Enabled."),
            _ => new ExternalSourceUnavailableException($"It answered {(int)status}.")
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {Source} sent a {EventType} that can't be read, which is skipped")]
    private partial void LogUnreadable(string source, string eventType);
}
