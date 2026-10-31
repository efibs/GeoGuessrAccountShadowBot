// SPDX-License-Identifier: GPL-2.0-or-later

using Discord;
using Discord.Interactions;
using GeoGuessrAccountShadowBot.Application.Control;
using GeoGuessrAccountShadowBot.Discord.Output;
using GeoGuessrAccountShadowBot.Domain;
using Microsoft.Extensions.Hosting;

namespace GeoGuessrAccountShadowBot.Discord.Input;

/// <summary>
/// The Accept and Decline buttons on friend request cards. Authorization happens in the application
/// layer. All answers are ephemeral, so a refusal stays between the bot and whoever clicked.
/// </summary>
public sealed class FriendRequestModule(AccountControlService control, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const string DeclineWarning =
        "Declining stops this person from sending another friend request, even if you become friends later. Decline anyway?";

    [ComponentInteraction("fr:a:*")]
    public async Task AcceptAsync(string userId)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await control.AcceptFriendRequestAsync(Context.User.Id, userId, lifetime.ApplicationStopping).ConfigureAwait(false);
        await FollowupAsync(Describe(result, "Accepted."), ephemeral: true, allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
    }

    /// <summary>A decline can't be undone from here, so it takes a second click.</summary>
    [ComponentInteraction("fr:d:*")]
    public Task DeclineAsync(string userId)
    {
        var confirm = new ComponentBuilder()
            .WithButton("Decline this request", FriendRequestButtonIds.ConfirmDecline(userId), ButtonStyle.Danger)
            .Build();

        return RespondAsync(DeclineWarning, components: confirm, ephemeral: true, allowedMentions: MentionPolicy.Nobody());
    }

    [ComponentInteraction("fr:c:*")]
    public async Task ConfirmDeclineAsync(string userId)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await control.DeclineFriendRequestAsync(Context.User.Id, userId, lifetime.ApplicationStopping).ConfigureAwait(false);
        await FollowupAsync(Describe(result, "Declined."), ephemeral: true, allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
    }

    private static string Describe(Result result, string success) => result.IsSuccess ? success : result.Error!.Message;
}

/// <summary>
/// Deliberate commands. Club chat uses a command rather than a channel, so that talk among staff
/// can't end up in front of the whole club by accident.
/// </summary>
[Group("shadow", "Act as the shadowed GeoGuessr account")]
public sealed class ShadowCommandsModule(AccountControlService control, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("club-say", "Post a message in the club chat as the account")]
    public async Task ClubSayAsync([Summary("message", "The text to post")] string message)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await control.SendClubMessageAsync(Context.User.Id, message, lifetime.ApplicationStopping).ConfigureAwait(false);
        var answer = result.IsSuccess ? "Sent." : result.Error!.Message;
        await FollowupAsync(answer, ephemeral: true, allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
    }
}
