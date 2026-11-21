// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Sources;

/// <summary>
/// The configured sources, by the name rules use. One the validator refuses is left out rather
/// than failing here, so that startup reports it with every other problem.
/// </summary>
public sealed class ExternalSources
{
    private readonly FrozenDictionary<string, IExternalSource> _sources;

    public ExternalSources(IOptions<ShadowOptions> options, IExternalSourceFactory factory) =>
        _sources = options.Value.Sources
            .Where(entry => ExternalSourceNames.IsValid(entry.Key) && entry.Value.Url is { IsAbsoluteUri: true })
            .ToFrozenDictionary(entry => entry.Key, entry => factory.Create(entry.Key, entry.Value), StringComparer.Ordinal);

    public IReadOnlyCollection<IExternalSource> All => _sources.Values;

    public IExternalSource? Find(string name) => _sources.GetValueOrDefault(name);
}

/// <summary>How the connection to one source stands.</summary>
/// <param name="Name">The source.</param>
/// <param name="Connected">Whether its event stream is open.</param>
/// <param name="Since">When it connected, or when it went down.</param>
/// <param name="Problem">Why it's down, if it is.</param>
/// <param name="LastEventAt">When the last event arrived here.</param>
/// <param name="LastDelay">How long the last event took from being recorded there to arriving here.</param>
public sealed record SourceStatus(string Name, bool Connected, DateTimeOffset Since, string? Problem, DateTimeOffset? LastEventAt, TimeSpan? LastDelay);

/// <summary>
/// Keeps the state of every followed source, and decides when the operators hear about it: once
/// a source has been down for its <see cref="SourceOptions.AlertAfter"/>, at once if it refused
/// the token, and again when it's back.
/// </summary>
public sealed class SourceStatusTracker(IOptions<ShadowOptions> options, TimeProvider timeProvider)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyList<SourceStatus> All
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Select(entry => entry.Status).OrderBy(status => status.Name, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>Starts following a source, which counts as down until its stream opens.</summary>
    public void Start(string name)
    {
        lock (_lock)
        {
            _entries[name] = new Entry(new SourceStatus(name, false, timeProvider.GetUtcNow(), "Connecting.", null, null), false);
        }
    }

    /// <returns>The alert to send, if the source was reported down.</returns>
    public OperatorAlertRaised? Connected(string name)
    {
        lock (_lock)
        {
            var entry = Get(name);
            if (entry.Status.Connected)
            {
                return null;
            }

            _entries[name] = new Entry(entry.Status with { Connected = true, Since = timeProvider.GetUtcNow(), Problem = null }, false);
            return entry.AlertOpen
                ? new OperatorAlertRaised($"Source {name} is back", "Its events are flowing again, and anything it recorded meanwhile is being caught up.", false)
                : null;
        }
    }

    /// <param name="refused">Whether the source refused this instance, which no retry will fix.</param>
    /// <returns>The alert to send, if this is the moment to send one.</returns>
    public OperatorAlertRaised? Down(string name, string problem, bool refused)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            var entry = Get(name);
            var status = entry.Status.Connected
                ? entry.Status with { Connected = false, Since = now, Problem = problem }
                : entry.Status with { Problem = problem };

            var due = !entry.AlertOpen && (refused || now - status.Since >= AlertAfter(name));
            _entries[name] = new Entry(status, entry.AlertOpen || due);
            if (!due)
            {
                return null;
            }

            return refused
                ? new OperatorAlertRaised($"Source {name} refused this instance", $"{problem} Rules reading it fail until the token or the source's ExternalApi:Clients are fixed.", true)
                : new OperatorAlertRaised($"Source {name} unreachable", $"{problem} Rules reading it wait and try again, and its events are caught up once it's back.", false);
        }
    }

    public void EventReceived(string name, DateTimeOffset recordedAt)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            var entry = Get(name);
            var delay = now - recordedAt;
            _entries[name] = entry with { Status = entry.Status with { LastEventAt = now, LastDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay } };
        }
    }

    /// <summary>False once a source has been down longer than its <see cref="SourceOptions.UnhealthyAfter"/>.</summary>
    public bool IsHealthy()
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            return _entries.Values.All(entry =>
                entry.Status.Connected
                || !options.Value.Sources.TryGetValue(entry.Status.Name, out var settings)
                || settings.UnhealthyAfter is not { } limit
                || now - entry.Status.Since < limit);
        }
    }

    private TimeSpan AlertAfter(string name) =>
        options.Value.Sources.TryGetValue(name, out var settings) ? settings.AlertAfter : TimeSpan.FromMinutes(5);

    private Entry Get(string name) =>
        _entries.TryGetValue(name, out var entry)
            ? entry
            : new Entry(new SourceStatus(name, false, timeProvider.GetUtcNow(), null, null, null), false);

    private sealed record Entry(SourceStatus Status, bool AlertOpen);
}
