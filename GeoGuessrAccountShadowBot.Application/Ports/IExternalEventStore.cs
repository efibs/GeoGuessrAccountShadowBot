// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Application.Ports;

/// <summary>An event recorded for the clients of the external API.</summary>
/// <param name="Id">Its place in the feed. Ids only grow, and clients resume from the last one they saw.</param>
/// <param name="Trigger">The trigger key, such as <c>ClubApplicationReceived</c>.</param>
/// <param name="SubjectUserId">The player it's about, if any.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="RecordedAt">When this instance recorded it.</param>
/// <param name="DataJson">What rules see as <c>event</c>.</param>
public sealed record ExternalEvent(long Id, string Trigger, string? SubjectUserId, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, string DataJson);

/// <summary>The first and last id in the feed, or zero for both while it's empty.</summary>
public sealed record ExternalEventBounds(long Oldest, long Newest);

/// <summary>
/// The feed of events served to other instances. It shares the unit of work of
/// <see cref="IShadowStore"/>, so an event is recorded in the transaction that processed it.
/// </summary>
public interface IExternalEventStore
{
    void AddExternalEvent(string trigger, string? subjectUserId, DateTimeOffset occurredAt, DateTimeOffset recordedAt, string dataJson);

    /// <summary>Events after an id with one of the triggers, oldest first.</summary>
    Task<IReadOnlyList<ExternalEvent>> ReadExternalEventsAsync(long after, IReadOnlyCollection<string> triggers, int limit, CancellationToken cancellationToken = default);

    Task<ExternalEventBounds> ReadExternalEventBoundsAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes events recorded before a moment, but always keeps the newest, which holds the feed's position.</summary>
    Task PruneExternalEventsAsync(DateTimeOffset recordedBefore, CancellationToken cancellationToken = default);
}
