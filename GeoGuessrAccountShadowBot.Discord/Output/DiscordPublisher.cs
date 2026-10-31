// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Discord.Rendering;
using GeoGuessrAccountShadowBot.Domain.Events;
using GeoGuessrAccountShadowBot.Domain.Model;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Discord.Output;

/// <summary>
/// The one path from the bot to Discord. The dispatcher runs deliveries one at a time, so two
/// messages from a new partner can't create two forum posts.
/// </summary>
internal sealed class DiscordPublisher(
    DiscordSocketClient client,
    IShadowStore store,
    PlayerDirectory players,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider) : IDiscordPublisher
{
    private const ThreadArchiveDuration ArchiveAfter = ThreadArchiveDuration.OneWeek;

    /// <summary>Discord.Net has no name for this error code.</summary>
    private const DiscordErrorCode ThreadIsLocked = (DiscordErrorCode)160005;

    private static readonly TimeSpan TitleCheckInterval = TimeSpan.FromHours(24);

    private ShadowOptions Settings => options.Value;

    public async Task<DeliveryResult> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            await (message switch
            {
                EventCard card => PublishCardAsync(card, cancellationToken),
                ChatLine line => PublishChatLineAsync(line, cancellationToken),
                FriendRequestCard request => PublishFriendRequestAsync(request, cancellationToken),
                FriendRequestOutcome outcome => PublishFriendRequestOutcomeAsync(outcome, cancellationToken),
                StatusMessage status => PublishStatusAsync(status),
                ReplyFeedback feedback => PublishReplyFeedbackAsync(feedback),
                _ => throw new InvalidOperationException($"Unknown outbox message {message.GetType().Name}.")
            }).ConfigureAwait(false);

            return DeliveryResult.Delivered;
        }
        catch (HttpException exception)
        {
            return Classify(exception);
        }
        catch (TimeoutException exception)
        {
            return new DeliveryResult(DeliveryStatus.Transient, exception.Message);
        }
        catch (MissingChannelException exception)
        {
            return new DeliveryResult(DeliveryStatus.Permanent, exception.Message);
        }
    }

    private async Task PublishCardAsync(EventCard card, CancellationToken cancellationToken)
    {
        var settings = Settings.Events.GetValueOrDefault(card.EventKey);
        var channel = await RequireChannelAsync<IMessageChannel>(settings?.ChannelId ?? Settings.Discord.DefaultChannelId).ConfigureAwait(false);
        var nick = card.ActorUserId is { } actor ? await players.GetNickAsync(actor, cancellationToken).ConfigureAwait(false) : (UntrustedText?)null;
        var roles = (IReadOnlyCollection<ulong>?)settings?.PingRoleIds ?? [];

        await channel.SendMessageAsync(
            MentionPolicy.PingLine(roles),
            embed: CardRenderer.Render(card, nick).ToEmbed(),
            allowedMentions: MentionPolicy.Roles(roles)).ConfigureAwait(false);
    }

    private async Task PublishChatLineAsync(ChatLine line, CancellationToken cancellationToken)
    {
        var nick = await players.GetNickAsync(line.PartnerUserId, cancellationToken).ConfigureAwait(false);
        var chunks = MessageChunker.Chunk(CardRenderer.RenderChatLine(line, nick, timeProvider.GetUtcNow()));

        var mapped = await store.FindPartnerThreadAsync(line.PartnerUserId, cancellationToken).ConfigureAwait(false);
        if (mapped is not null && await TrySendToThreadAsync(mapped, chunks, nick, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await CreatePostAsync(line.PartnerUserId, nick, chunks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends to an existing post, and returns false if that post no longer exists.</summary>
    private async Task<bool> TrySendToThreadAsync(PartnerThread mapped, IReadOnlyList<string> chunks, UntrustedText nick, CancellationToken cancellationToken)
    {
        var thread = await FindChannelAsync<IThreadChannel>(mapped.ThreadId).ConfigureAwait(false);
        if (thread is null)
        {
            return false;
        }

        try
        {
            await SendChunksAsync(thread, chunks).ConfigureAwait(false);
        }
        catch (HttpException exception) when (exception.DiscordCode == DiscordErrorCode.UnknownChannel)
        {
            return false;
        }
        catch (HttpException exception) when (exception.DiscordCode == ThreadIsLocked)
        {
            await thread.ModifyAsync(properties =>
            {
                properties.Locked = false;
                properties.Archived = false;
            }).ConfigureAwait(false);
            await SendChunksAsync(thread, chunks).ConfigureAwait(false);
        }

        await RefreshTitleIfDueAsync(thread, mapped, nick, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task CreatePostAsync(string partnerUserId, UntrustedText nick, IReadOnlyList<string> chunks, CancellationToken cancellationToken)
    {
        var forumId = Settings.Forum.ChannelId ?? throw new MissingChannelException("No forum channel is configured.");
        var forum = await RequireChannelAsync<IForumChannel>(forumId).ConfigureAwait(false);
        var title = ForumTitle.Create(nick, partnerUserId);

        var post = await forum.CreatePostAsync(
            title,
            ArchiveAfter,
            text: chunks[0],
            allowedMentions: MentionPolicy.Nobody(),
            flags: MessageFlags.SuppressEmbeds).ConfigureAwait(false);

        await store.SavePartnerThreadAsync(new PartnerThread(partnerUserId, post.Id, title, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        await SendChunksAsync(post, chunks.Skip(1)).ConfigureAwait(false);
    }

    private static async Task SendChunksAsync(IMessageChannel channel, IEnumerable<string> chunks)
    {
        foreach (var chunk in chunks)
        {
            await channel.SendMessageAsync(chunk, allowedMentions: MentionPolicy.Nobody(), flags: MessageFlags.SuppressEmbeds).ConfigureAwait(false);
        }
    }

    /// <summary>Follows a partner's rename, at most daily, because Discord limits renames strictly.</summary>
    private async Task RefreshTitleIfDueAsync(IThreadChannel thread, PartnerThread mapped, UntrustedText nick, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - mapped.TitleCheckedAt < TitleCheckInterval)
        {
            return;
        }

        var title = ForumTitle.Create(nick, mapped.PartnerUserId);
        if (title != mapped.TitleNick)
        {
            await thread.ModifyAsync(properties => properties.Name = title).ConfigureAwait(false);
        }

        await store.SavePartnerThreadAsync(mapped with { TitleNick = title, TitleCheckedAt = now }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishFriendRequestAsync(FriendRequestCard request, CancellationToken cancellationToken)
    {
        var settings = Settings.Events.GetValueOrDefault(EventKeys.FriendRequestReceived);
        var channel = await RequireChannelAsync<IMessageChannel>(settings?.ChannelId ?? Settings.Discord.DefaultChannelId).ConfigureAwait(false);
        var nick = await players.GetNickAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var roles = (IReadOnlyCollection<ulong>?)settings?.PingRoleIds ?? [];

        var components = Settings.Control.FriendRequestButtons.Enabled
            ? new ComponentBuilder()
                .WithButton("Accept", FriendRequestButtonIds.Accept(request.UserId), ButtonStyle.Success)
                .WithButton("Decline", FriendRequestButtonIds.Decline(request.UserId), ButtonStyle.Danger)
                .Build()
            : null;

        var message = await channel.SendMessageAsync(
            MentionPolicy.PingLine(roles),
            embed: CardRenderer.RenderFriendRequest(nick).ToEmbed(),
            components: components,
            allowedMentions: MentionPolicy.Roles(roles)).ConfigureAwait(false);

        await store.SaveFriendRequestCardAsync(new FriendRequestCardLocation(request.UserId, channel.Id, message.Id), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishFriendRequestOutcomeAsync(FriendRequestOutcome outcome, CancellationToken cancellationToken)
    {
        var card = await store.FindFriendRequestCardAsync(outcome.UserId, cancellationToken).ConfigureAwait(false);
        if (card is null)
        {
            return;
        }

        await store.RemoveFriendRequestCardAsync(outcome.UserId, cancellationToken).ConfigureAwait(false);

        var channel = await FindChannelAsync<IMessageChannel>(card.ChannelId).ConfigureAwait(false);
        if (channel is null || await channel.GetMessageAsync(card.MessageId).ConfigureAwait(false) is not IUserMessage message)
        {
            return;
        }

        var nick = await players.GetNickAsync(outcome.UserId, cancellationToken).ConfigureAwait(false);
        var embed = CardRenderer.RenderFriendRequestOutcome(nick, outcome.BecameFriend).ToEmbed();

        await message.ModifyAsync(properties =>
        {
            properties.Embed = embed;
            properties.Components = new ComponentBuilder().Build();
            properties.AllowedMentions = MentionPolicy.Nobody();
        }).ConfigureAwait(false);
    }

    private async Task PublishStatusAsync(StatusMessage status)
    {
        var discord = Settings.Discord;
        var channel = await RequireChannelAsync<IMessageChannel>(discord.StatusChannelId ?? discord.DefaultChannelId).ConfigureAwait(false);
        IReadOnlyCollection<ulong> roles = status.Ping ? discord.StatusPingRoleIds : [];

        await channel.SendMessageAsync(
            MentionPolicy.PingLine(roles),
            embed: CardRenderer.RenderStatus(status).ToEmbed(),
            allowedMentions: MentionPolicy.Roles(roles)).ConfigureAwait(false);
    }

    private async Task PublishReplyFeedbackAsync(ReplyFeedback feedback)
    {
        var channel = await FindChannelAsync<IMessageChannel>(feedback.ThreadId).ConfigureAwait(false);
        if (channel is null || await channel.GetMessageAsync(feedback.MessageId).ConfigureAwait(false) is not { } message)
        {
            return;
        }

        var reaction = feedback.Kind switch
        {
            ReplyFeedbackKind.Delivered => "✅",
            ReplyFeedbackKind.Unconfirmed => "⚠️",
            ReplyFeedbackKind.NotSent => "❌",
            _ => "🚫"
        };
        await message.AddReactionAsync(new Emoji(reaction)).ConfigureAwait(false);

        if (feedback.Reason is { } reason)
        {
            await channel.SendMessageAsync(
                reason,
                messageReference: new MessageReference(feedback.MessageId),
                allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
        }
    }

    private async Task<T> RequireChannelAsync<T>(ulong channelId) where T : class, IChannel =>
        await FindChannelAsync<T>(channelId).ConfigureAwait(false)
        ?? throw new MissingChannelException($"Channel {channelId} doesn't exist or isn't a {typeof(T).Name}.");

    /// <summary>Asks the REST API if the cache misses, which is the normal case for archived posts.</summary>
    private async Task<T?> FindChannelAsync<T>(ulong channelId) where T : class, IChannel
    {
        if (client.GetChannel(channelId) is T cached)
        {
            return cached;
        }

        try
        {
            return await client.Rest.GetChannelAsync(channelId).ConfigureAwait(false) as T;
        }
        catch (HttpException exception) when (exception.DiscordCode == DiscordErrorCode.UnknownChannel)
        {
            return null;
        }
    }

    private static DeliveryResult Classify(HttpException exception)
    {
        var isTransient = exception.HttpCode == HttpStatusCode.TooManyRequests || (int)exception.HttpCode >= 500;
        return new DeliveryResult(
            isTransient ? DeliveryStatus.Transient : DeliveryStatus.Permanent,
            $"Discord answered {(int)exception.HttpCode} ({exception.DiscordCode})");
    }

    private sealed class MissingChannelException(string message) : Exception(message);
}
