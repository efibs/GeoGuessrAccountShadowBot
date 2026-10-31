// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain;

namespace GeoGuessrAccountShadowBot.Application.Ports;

/// <summary>
/// Sends chat messages as the account. Sends are fire-and-forget at the socket level: the only
/// confirmation is the message coming back on the inbox subscription.
/// </summary>
public interface IGeoGuessrChatSender
{
    Task<Result> SendDirectMessageAsync(string peerId, string text, CancellationToken cancellationToken = default);

    Task<Result> SendClubMessageAsync(string clubId, string text, CancellationToken cancellationToken = default);
}

/// <summary>How a Discord delivery went.</summary>
public enum DeliveryStatus
{
    Delivered,

    /// <summary>Worth retrying, for example while Discord is down.</summary>
    Transient,

    /// <summary>Can't succeed, for example because a channel is gone.</summary>
    Permanent
}

/// <summary>The result of one Discord delivery.</summary>
public sealed record DeliveryResult(DeliveryStatus Status, string? Reason = null)
{
    public static readonly DeliveryResult Delivered = new(DeliveryStatus.Delivered);
}

/// <summary>
/// The one path to Discord. A new instance serves each delivery, within one store unit of work.
/// </summary>
public interface IDiscordPublisher
{
    Task<DeliveryResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Tells the dispatcher when Discord can be reached.</summary>
public interface IDiscordReadiness
{
    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a member's current roles. It must not use a gateway cache, which can hold stale roles,
/// because the answer decides who may act as the account.
/// </summary>
public interface IGuildRoleReader
{
    Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong discordUserId, CancellationToken cancellationToken = default);
}
