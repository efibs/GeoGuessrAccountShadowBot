// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Application.Reconciliation;
using GeoGuessrAccountShadowBot.Domain;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Control;

/// <summary>
/// The actions behind Discord's buttons and commands.
/// </summary>
public sealed class AccountControlService(
    ControlCommandRunner runner,
    IGeoGuessrApi api,
    IGeoGuessrChatSender sender,
    AccountContext account,
    ConnectionStatus connection,
    ReconcileRequests reconcile,
    IOptions<ShadowOptions> options)
{
    private static readonly Error NoLongerPending =
        new(ErrorKind.NotFound, "friend-request.not_pending", "This request is no longer pending.");

    public Task<Result> AcceptFriendRequestAsync(ulong discordUserId, string userId, CancellationToken cancellationToken) =>
        RunFriendActionAsync(discordUserId, "friend-accept", userId, api.AcceptFriendRequestAsync, cancellationToken);

    public Task<Result> DeclineFriendRequestAsync(ulong discordUserId, string userId, CancellationToken cancellationToken) =>
        RunFriendActionAsync(discordUserId, "friend-decline", userId, api.DeclineFriendRequestAsync, cancellationToken);

    public Task<Result> SendClubMessageAsync(ulong discordUserId, string text, CancellationToken cancellationToken)
    {
        var settings = options.Value.Control.ClubChat;

        return runner.RunAsync(discordUserId, "club-say", account.ClubId ?? "none", ct =>
        {
            if (!settings.Enabled)
            {
                return Task.FromResult<Result>(new Error(ErrorKind.Forbidden, "club-chat.disabled", "Posting in club chat is switched off."));
            }

            if (account.ClubId is not { } clubId)
            {
                return Task.FromResult<Result>(new Error(ErrorKind.NotFound, "club-chat.no_club", "This account isn't in a club."));
            }

            if (string.IsNullOrWhiteSpace(text) || text.Length > settings.MaxLength)
            {
                return Task.FromResult<Result>(new Error(ErrorKind.Validation, "club-chat.length", $"Messages must have between 1 and {settings.MaxLength} characters."));
            }

            if (connection.Current.Phase != ConnectionPhase.Connected)
            {
                return Task.FromResult<Result>(new Error(ErrorKind.Unavailable, "club-chat.offline", "The bot isn't connected to GeoGuessr right now."));
            }

            return sender.SendClubMessageAsync(clubId, text, ct);
        }, cancellationToken);
    }

    private Task<Result> RunFriendActionAsync(
        ulong discordUserId,
        string action,
        string userId,
        Func<string, CancellationToken, Task<Result>> friendAction,
        CancellationToken cancellationToken) =>
        runner.RunAsync(discordUserId, action, userId, async ct =>
        {
            if (!options.Value.Control.FriendRequestButtons.Enabled)
            {
                return new Error(ErrorKind.Forbidden, "friend-request.disabled", "Answering friend requests is switched off.");
            }

            // A DELETE on someone who is already a friend would end the friendship, and a card can
            // be stale, so the request has to be confirmed as still pending right before acting.
            var friendships = await api.GetFriendshipsAsync(ct).ConfigureAwait(false);
            if (!friendships.Incoming.Contains(userId))
            {
                return NoLongerPending;
            }

            var result = await friendAction(userId, ct).ConfigureAwait(false);
            reconcile.Request(ReconcileScope.Friends);
            return result;
        }, cancellationToken);
}
