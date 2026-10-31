// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Model;
using Microsoft.Extensions.Logging;

namespace GeoGuessrAccountShadowBot.Application.Notifications;

/// <summary>
/// Resolves GeoGuessr user ids to names at delivery time, so that a slow lookup holds up one
/// delivery rather than the event pipeline. A lookup failure falls back to a short id, because a
/// message without a name is still better than one that never arrives.
/// </summary>
public sealed partial class PlayerDirectory(
    IShadowStore store,
    IGeoGuessrApi api,
    TimeProvider timeProvider,
    ILogger<PlayerDirectory> logger)
{
    private const int ShortIdLength = 8;

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

    public static UntrustedText Fallback(string userId) =>
        new($"player-{userId[..Math.Min(ShortIdLength, userId.Length)]}");

    public async Task<UntrustedText> GetNickAsync(string userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cached = await store.FindPlayerNameAsync(userId, cancellationToken).ConfigureAwait(false);
        if (cached is not null && now - cached.FetchedAt < CacheFor)
        {
            return new UntrustedText(cached.Nick);
        }

        try
        {
            if (await api.GetPlayerNickAsync(userId, cancellationToken).ConfigureAwait(false) is { } nick)
            {
                await store.SavePlayerNameAsync(new PlayerName(userId, nick, now), cancellationToken).ConfigureAwait(false);
                return new UntrustedText(nick);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLookupFailed(exception, userId);
        }

        return cached is not null ? new UntrustedText(cached.Nick) : Fallback(userId);
    }

    /// <summary>
    /// Reads a full profile, for the moments it's worth the extra requests. It's never cached, since
    /// someone's standing should be current when you decide about them. Returns null on failure,
    /// because a friend request has to be shown either way.
    /// </summary>
    public async Task<PlayerProfile?> GetProfileAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await api.GetPlayerProfileAsync(userId, cancellationToken).ConfigureAwait(false);
            if (profile is not null)
            {
                await store.SavePlayerNameAsync(new PlayerName(userId, profile.Nick, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            }

            return profile;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLookupFailed(exception, userId);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not look up the name of player {UserId}")]
    private partial void LogLookupFailed(Exception exception, string userId);
}
