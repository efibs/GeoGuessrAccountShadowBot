// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Domain.Events;

/// <summary>
/// Something that happened on the shadowed account. Every event, whether it came from the socket or
/// from HTTP reconciliation, passes through the same pipeline.
/// </summary>
public abstract record ShadowEvent
{
    /// <summary>
    /// The key that identifies this event across sources and restarts. Null means the event is
    /// transient, so nothing is recorded and it can't be recognised as a repeat.
    /// </summary>
    public virtual string? DedupeKey => null;

    /// <summary>
    /// The entry in <see cref="EventKeys"/> that switches this event on or off. Null means the event
    /// is internal and always runs.
    /// </summary>
    public virtual string? SwitchKey => null;

    /// <summary>
    /// When true, the event updates stored state but notifies nobody. The first sync uses this to
    /// record what already exists without flooding Discord.
    /// </summary>
    public bool Silent { get; init; }
}
