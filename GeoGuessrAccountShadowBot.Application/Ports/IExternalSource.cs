// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Nodes;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;

namespace GeoGuessrAccountShadowBot.Application.Ports;

/// <summary>A source couldn't be reached, or answered with an error that may pass.</summary>
public sealed class ExternalSourceUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// A source refused the request, because the token is wrong or this instance may not read what
/// it asked for. Trying again won't help until someone changes a configuration.
/// </summary>
public sealed class ExternalSourceRefusedException(string message) : Exception(message);

/// <summary>Something that came down a source's event stream.</summary>
public abstract record SourceStreamItem;

/// <summary>Sent first on every connection: where the source's feed stands.</summary>
/// <param name="Oldest">The oldest event it still has, or zero if none.</param>
/// <param name="Newest">The newest event, or zero if there never was one.</param>
public sealed record SourceHello(long Oldest, long Newest) : SourceStreamItem;

/// <summary>An event of the source.</summary>
/// <param name="Id">Its position in the source's feed.</param>
/// <param name="Trigger">The trigger at the source, such as <c>ClubApplicationReceived</c>.</param>
/// <param name="PlayerId">The player it's about, if any.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="RecordedAt">When the source recorded it, by its clock.</param>
/// <param name="Data">What rules see as <c>event</c>.</param>
public sealed record SourceEvent(long Id, string Trigger, string? PlayerId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, JsonObject Data) : SourceStreamItem;

/// <summary>
/// Another system that rules read from, such as another instance of the bot. A source has data in
/// named sections, which rules read as <c>sources.name.section</c>, and may have events, which
/// start rules with <c>On: Source:name:trigger</c>.
/// </summary>
public interface IExternalSource
{
    string Name { get; }

    /// <summary>The sections rules can read, such as <c>player</c>.</summary>
    IReadOnlySet<string> Sections { get; }

    /// <summary>The sections that are about the run's player, and so need one.</summary>
    IReadOnlySet<string> PlayerSections { get; }

    bool HasEvents { get; }

    /// <summary>Reads one section, for the run's player if it needs one.</summary>
    /// <exception cref="ExternalSourceUnavailableException">The source can't be reached right now.</exception>
    /// <exception cref="ExternalSourceRefusedException">The source won't give this out.</exception>
    Task<JsonObject> ReadSectionAsync(string section, string? playerId, CancellationToken cancellationToken = default);

    /// <summary>The players in one of the source's groups, for a Raise with ForEach.</summary>
    /// <exception cref="ExternalSourceUnavailableException">The source can't be reached right now.</exception>
    /// <exception cref="ExternalSourceRefusedException">The source won't give this out.</exception>
    Task<IReadOnlyList<string>> ReadAudienceAsync(AutomationAudience audience, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams events after a position, starting with a <see cref="SourceHello"/>. It ends or
    /// throws when the connection does, and the caller reconnects.
    /// </summary>
    /// <param name="after">The last event handled, or null to start from now.</param>
    /// <exception cref="ExternalSourceUnavailableException">The source can't be reached right now.</exception>
    /// <exception cref="ExternalSourceRefusedException">The source won't give this out.</exception>
    IAsyncEnumerable<SourceStreamItem> StreamEventsAsync(long? after, CancellationToken cancellationToken = default);
}

/// <summary>Makes the client for a configured source.</summary>
public interface IExternalSourceFactory
{
    IExternalSource Create(string name, SourceOptions options);
}

/// <summary>Sent while nothing happens, so both ends notice a dead connection.</summary>
public sealed record SourcePing : SourceStreamItem;
