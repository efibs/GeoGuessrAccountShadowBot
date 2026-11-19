// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Sources;

namespace GeoGuessrAccountShadowBot.Tests.TestSupport;

/// <summary>Hands out a <see cref="FakeSource"/> per configured source, for tests to script.</summary>
internal sealed class FakeSources : IExternalSourceFactory
{
    private readonly ConcurrentDictionary<string, FakeSource> _sources = new(StringComparer.Ordinal);

    public FakeSource this[string name] => _sources.GetOrAdd(name, key => new FakeSource(key));

    public IExternalSource Create(string name, SourceOptions options) => this[name];
}

/// <summary>A source whose answers and failures a test sets.</summary>
internal sealed class FakeSource(string name) : IExternalSource
{
    private Channel<SourceStreamItem> _stream = Channel.CreateUnbounded<SourceStreamItem>();

    public string Name => name;

    public IReadOnlySet<string> Sections => ExternalSourceNames.ShadowBotSections;

    public IReadOnlySet<string> PlayerSections { get; } = new HashSet<string> { ExternalSourceNames.PlayerSection };

    public bool HasEvents => true;

    /// <summary>The players by id, as the source would answer for them.</summary>
    public Dictionary<string, JsonObject> Players { get; } = [];

    public JsonObject Account { get; set; } = new() { ["id"] = "cccccccccccccccccccccccc", ["club_id"] = "remote-club" };

    public Dictionary<AutomationAudience, string[]> Audiences { get; } = [];

    /// <summary>Thrown by every read while set, to act out an outage or a refusal.</summary>
    public Exception? Failure { get; set; }

    public List<string> Reads { get; } = [];

    /// <summary>Where each stream was asked to start from.</summary>
    public List<long?> StreamRequests { get; } = [];

    public Task<JsonObject> ReadSectionAsync(string section, string? playerId, CancellationToken cancellationToken = default)
    {
        lock (Reads)
        {
            Reads.Add(playerId is null ? section : $"{section}:{playerId}");
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(section == ExternalSourceNames.AccountSection
            ? Account
            : Players.TryGetValue(playerId!, out var player) ? player : new JsonObject { ["id"] = playerId, ["has_applied"] = false });
    }

    public Task<IReadOnlyList<string>> ReadAudienceAsync(AutomationAudience audience, CancellationToken cancellationToken = default) =>
        Failure is { } failure ? throw failure : Task.FromResult<IReadOnlyList<string>>(Audiences.GetValueOrDefault(audience) ?? []);

    public void Send(SourceStreamItem item) => _stream.Writer.TryWrite(item);

    /// <summary>Ends the current stream, as a dropped connection would.</summary>
    public void Disconnect()
    {
        var old = _stream;
        _stream = Channel.CreateUnbounded<SourceStreamItem>();
        old.Writer.TryComplete(new ExternalSourceUnavailableException("The connection dropped."));
    }

    public async IAsyncEnumerable<SourceStreamItem> StreamEventsAsync(long? after, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamRequests.Add(after);
        if (Failure is { } failure)
        {
            throw failure;
        }

        await foreach (var item in _stream.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }
}
