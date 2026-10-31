// SPDX-License-Identifier: GPL-2.0-or-later

using Discord;
using Discord.WebSocket;
using GeoGuessrAccountShadowBot.Application.Control;
using GeoGuessrAccountShadowBot.Application.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Discord.Input;

/// <summary>
/// The plain facts about a Discord message, so the decision can be tested without Discord's types.
/// </summary>
public sealed record InboundMessageFacts(MessageSource Source, ulong? GuildId, ulong? ParentChannelId);

/// <summary>
/// Decides whether a message could be a reply. Bots, webhooks and system messages never are, and
/// that's also what stops the bot from answering its own mirrored messages.
/// </summary>
public static class InboundMessageFilter
{
    public static bool IsForumReply(InboundMessageFacts facts, ulong guildId, ulong? forumChannelId) =>
        facts.Source == MessageSource.User
        && facts.GuildId == guildId
        && forumChannelId is not null
        && facts.ParentChannelId == forumChannelId;
}

/// <summary>
/// Passes messages typed in partner posts to <see cref="DmReplyService"/>. Gateway handlers block
/// Discord.Net's event loop, so the work moves off it straight away.
/// </summary>
internal sealed partial class ForumReplyListener(
    DiscordSocketClient client,
    DmReplyService replies,
    IHostApplicationLifetime lifetime,
    IOptions<ShadowOptions> options,
    ILogger<ForumReplyListener> logger)
{
    public void Attach()
    {
        if (options.Value.Control.DmReply.Enabled)
        {
            client.MessageReceived += OnMessageReceivedAsync;
        }
    }

    public void Detach() => client.MessageReceived -= OnMessageReceivedAsync;

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        var thread = message.Channel as SocketThreadChannel;
        var facts = new InboundMessageFacts(message.Source, thread?.Guild.Id, thread?.ParentChannel?.Id);

        if (thread is not null && InboundMessageFilter.IsForumReply(facts, options.Value.Discord.GuildId, options.Value.Forum.ChannelId))
        {
            _ = Task.Run(() => ForwardAsync(message, thread.Id), lifetime.ApplicationStopping);
        }

        return Task.CompletedTask;
    }

    private async Task ForwardAsync(SocketMessage message, ulong threadId)
    {
        try
        {
            var reply = new ForumReply(
                threadId,
                message.Id,
                message.Author.Id,
                message.CleanContent,
                message.Attachments.Count > 0 || message.Stickers.Count > 0);

            await replies.HandleAsync(reply, lifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogForwardFailed(exception, message.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling the forum reply {MessageId} failed")]
    private partial void LogForwardFailed(Exception exception, ulong messageId);
}
