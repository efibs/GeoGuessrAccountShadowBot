// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Sources;

/// <summary>A client of the external API, and what it may see.</summary>
public sealed record ExternalApiClient(string Name, FrozenSet<string> Events, FrozenSet<ExternalApiData> Data)
{
    public bool May(ExternalApiData data) => Data.Contains(data);
}

/// <summary>The account isn't known yet, so there is nothing to serve.</summary>
public sealed class ExternalApiNotReadyException() : Exception("The bot is still starting.");

/// <summary>
/// What the external API serves, without anything of HTTP. It reads only what the bot already
/// knows, so serving another instance never costs a GeoGuessr call.
/// </summary>
public sealed class ExternalApiService
{
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly AccountContext _account;
    private readonly ExternalFeedSignal _signal;
    private readonly TimeProvider _timeProvider;
    private readonly (byte[] Hash, ExternalApiClient Client)[] _clients;

    public ExternalApiService(
        IOptions<ShadowOptions> options,
        IServiceScopeFactory scopes,
        AccountContext account,
        ExternalFeedSignal signal,
        TimeProvider timeProvider)
    {
        _scopes = scopes;
        _account = account;
        _signal = signal;
        _timeProvider = timeProvider;
        _clients = options.Value.ExternalApi.Clients
            .Where(entry => !string.IsNullOrEmpty(entry.Value.Token))
            .Select(entry => (Hash(entry.Value.Token), new ExternalApiClient(
                entry.Key,
                entry.Value.Events.Select(ExternalEventFeed.Canonical).OfType<string>().ToFrozenSet(StringComparer.Ordinal),
                entry.Value.Data.ToFrozenSet())))
            .ToArray();
    }

    /// <summary>
    /// Finds the client a token belongs to. Hashes are compared, in constant time and against every
    /// client, so neither the timing nor the length of a guess tells anything.
    /// </summary>
    public ExternalApiClient? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Hash(token);
        ExternalApiClient? found = null;
        foreach (var (expected, client) in _clients)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                found = client;
            }
        }

        return found;
    }

    public JsonObject ReadAccount()
    {
        EnsureReady();
        return new JsonObject { ["id"] = _account.UserId, ["club_id"] = _account.ClubId };
    }

    public async Task<JsonObject> ReadPlayerAsync(string userId, CancellationToken cancellationToken)
    {
        EnsureReady();
        var relations = await WithStoreAsync(store => PlayerRelations.ReadAsync(store, userId, cancellationToken)).ConfigureAwait(false);
        return new JsonObject
        {
            ["id"] = userId,
            ["is_friend"] = relations.IsFriend,
            ["has_requested"] = relations.HasRequested,
            ["was_requested"] = relations.WasRequested,
            ["is_club_member"] = relations.IsClubMember,
            ["has_applied"] = relations.HasApplied
        };
    }

    public Task<IReadOnlyList<string>> ReadAudienceAsync(AutomationAudience audience, CancellationToken cancellationToken)
    {
        EnsureReady();
        return WithStoreAsync(store => AutomationAudiences.ReadAsync(store, audience, _account.UserId, cancellationToken));
    }

    /// <summary>
    /// Streams the events a client may see. It greets with where the feed stands, sends everything
    /// after <paramref name="after"/>, then waits for more, pinging while nothing happens. A client
    /// that is ahead of the feed, because this database started over, is sent from the newest.
    /// </summary>
    public async IAsyncEnumerable<SourceStreamItem> StreamAsync(
        ExternalApiClient client, long? after, TimeSpan pingInterval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var bounds = await WithFeedAsync(store => store.ReadExternalEventBoundsAsync(cancellationToken)).ConfigureAwait(false);
        yield return new SourceHello(bounds.Oldest, bounds.Newest);

        var cursor = after is { } last && last <= bounds.Newest ? last : bounds.Newest;
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = _signal.Next;
            var batch = await WithFeedAsync(store => store.ReadExternalEventsAsync(cursor, client.Events, BatchSize, cancellationToken)).ConfigureAwait(false);
            foreach (var row in batch)
            {
                cursor = row.Id;
                yield return new SourceEvent(row.Id, row.Trigger, row.SubjectUserId, row.OccurredAt, row.RecordedAt, ParseData(row.DataJson));
            }

            if (batch.Count == BatchSize)
            {
                continue;
            }

            if (!await ExternalFeedSignal.WaitAsync(next, pingInterval, _timeProvider, cancellationToken).ConfigureAwait(false))
            {
                yield return new SourcePing();
            }
        }
    }

    private void EnsureReady()
    {
        if (!_account.Ready.IsCompleted)
        {
            throw new ExternalApiNotReadyException();
        }
    }

    private static JsonObject ParseData(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private async Task<T> WithStoreAsync<T>(Func<IShadowStore, Task<T>> action)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await action(scope.ServiceProvider.GetRequiredService<IShadowStore>()).ConfigureAwait(false);
        }
    }

    private async Task<T> WithFeedAsync<T>(Func<IExternalEventStore, Task<T>> action)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await action(scope.ServiceProvider.GetRequiredService<IExternalEventStore>()).ConfigureAwait(false);
        }
    }
}
