// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Pipeline;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using Microsoft.Extensions.Logging;

namespace GeoGuessrAccountShadowBot.Application.Handlers;

/// <summary>
/// Compares a club roster with the stored one.
/// </summary>
public sealed partial class ClubRosterFetchedHandler(
    IShadowStore store,
    IEventContext context,
    ILogger<ClubRosterFetchedHandler> logger) : IShadowEventHandler<ClubRosterFetched>
{
    private const string StateKey = "club-roster";

    public async ValueTask HandleAsync(ClubRosterFetched shadowEvent, CancellationToken cancellationToken)
    {
        var current = shadowEvent.Roster;
        var previous = await store.ReadStateAsync<ClubRoster>(StateKey, cancellationToken).ConfigureAwait(false);

        if (previous is not null)
        {
            if (current.LooksLikeGlitchComparedTo(previous))
            {
                LogSnapshotIgnored(previous.Members.Count, current.Members.Count);
                return;
            }

            var diff = current.DiffFrom(previous);
            RaiseAll(diff.Joined, ClubMemberChange.Joined);
            RaiseAll(diff.Left, ClubMemberChange.Left);
            RaiseAll(diff.RoleChanged, ClubMemberChange.RoleChanged);
        }

        await store.WriteStateAsync(StateKey, current, cancellationToken).ConfigureAwait(false);
    }

    private void RaiseAll(IEnumerable<ClubMember> members, ClubMemberChange change)
    {
        foreach (var member in members)
        {
            context.Raise(new ClubMemberChanged(member, change));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored a club roster that went from {Before} to {After} members")]
    private partial void LogSnapshotIgnored(int before, int after);
}

/// <summary>
/// Announces a change to a club member.
/// </summary>
public sealed class ClubMemberChangedHandler(IEventContext context) : IShadowEventHandler<ClubMemberChanged>
{
    public ValueTask HandleAsync(ClubMemberChanged shadowEvent, CancellationToken cancellationToken)
    {
        var (style, title) = shadowEvent.Change switch
        {
            ClubMemberChange.Joined => (CardStyle.Success, "Member joined the club"),
            ClubMemberChange.Left => (CardStyle.Warning, "Member left the club"),
            _ => (CardStyle.Info, "Member role changed")
        };

        context.Notify(new EventCard(shadowEvent.SwitchKey, style, title, null, shadowEvent.Member.UserId, null, null));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Announces progress on the club's mission board.
/// </summary>
public sealed class ClubMissionBoardHandler(IEventContext context) : IShadowEventHandler<ClubMissionBoardProgressed>
{
    public ValueTask HandleAsync(ClubMissionBoardProgressed shadowEvent, CancellationToken cancellationToken)
    {
        var title = shadowEvent.BoardCleared ? "Mission board cleared" : "Mission board progressed";
        context.Notify(new EventCard(EventKeys.ClubMissionBoard, CardStyle.Info, title, null, shadowEvent.ContributorUserId, null, null));
        return ValueTask.CompletedTask;
    }
}
