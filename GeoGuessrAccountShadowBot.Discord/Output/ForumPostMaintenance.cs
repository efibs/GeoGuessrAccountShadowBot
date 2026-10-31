// SPDX-License-Identifier: GPL-2.0-or-later

using Discord;
using Discord.Net;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Discord.Rendering;
using GeoGuessrAccountShadowBot.Domain.Model;
using Microsoft.Extensions.Logging;

namespace GeoGuessrAccountShadowBot.Discord.Output;

/// <summary>
/// Keeps an existing post current: its title follows the partner's name, and posts created before
/// profile links existed get one. It runs at most daily, because Discord limits renames strictly.
/// All of this happens after the message was delivered, so a failure here is only worth a warning.
/// Treating it as a failed delivery would send the message a second time.
/// </summary>
internal sealed partial class ForumPostMaintenance(
    IShadowStore store,
    TimeProvider timeProvider,
    ILogger<ForumPostMaintenance> logger)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public async Task RefreshIfDueAsync(IThreadChannel thread, PartnerThread mapped, UntrustedText nick, ulong botUserId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - mapped.TitleCheckedAt < CheckInterval)
        {
            return;
        }

        var title = ForumTitle.Create(nick, mapped.PartnerUserId);

        try
        {
            if (title != mapped.TitleNick)
            {
                await thread.ModifyAsync(properties => properties.Name = title).ConfigureAwait(false);
            }

            await AddProfileLinkIfMissingAsync(thread, mapped.PartnerUserId, botUserId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpException or TimeoutException)
        {
            // The check stays due, so the next message tries again.
            LogRefreshFailed(exception, thread.Id);
            return;
        }

        await store.SavePartnerThreadAsync(mapped with { TitleNick = title, TitleCheckedAt = now }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A forum post shares its id with its first message.</summary>
    private static async Task AddProfileLinkIfMissingAsync(IThreadChannel thread, string partnerUserId, ulong botUserId)
    {
        var starter = await thread.GetMessageAsync(thread.Id).ConfigureAwait(false) as IUserMessage;
        if (starter is null || starter.Author.Id != botUserId || CardRenderer.HasProfileLink(starter.Content, partnerUserId))
        {
            return;
        }

        await starter.ModifyAsync(properties =>
        {
            properties.Content = CardRenderer.WithProfileLink(starter.Content, partnerUserId);
            properties.AllowedMentions = MentionPolicy.Nobody();
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not refresh forum post {ThreadId}, and will try again with the next message")]
    private partial void LogRefreshFailed(Exception exception, ulong threadId);
}
