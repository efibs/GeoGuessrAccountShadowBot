// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Connection;
using GeoGuessrAccountShadowBot.Application.Handlers;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Control;

/// <summary>
/// A message someone typed in a partner's forum post.
/// </summary>
/// <param name="ThreadId">The forum post.</param>
/// <param name="MessageId">The Discord message.</param>
/// <param name="AuthorId">Who typed it.</param>
/// <param name="Text">Its text, with Discord mentions already resolved to names.</param>
/// <param name="HasOtherContent">It has attachments or stickers, which GeoGuessr chat can't carry.</param>
public sealed record ForumReply(ulong ThreadId, ulong MessageId, ulong AuthorId, string Text, bool HasOtherContent);

/// <summary>
/// Sends a message typed in Discord as a GeoGuessr DM. Sends are never retried and never queued: a
/// message appearing minutes late, or twice, would be worse than telling its author it failed.
/// </summary>
public sealed class DmReplyService(
    IServiceScopeFactory scopes,
    ControlCommandRunner runner,
    IGeoGuessrChatSender sender,
    ConnectionStatus connection,
    OutboxSignal outboxSignal,
    IOptions<ShadowOptions> options,
    TimeProvider timeProvider)
{
    private readonly Queue<DateTimeOffset> _recentSends = new();
    private readonly Lock _rateLock = new();

    public async Task HandleAsync(ForumReply reply, CancellationToken cancellationToken)
    {
        var settings = options.Value.Control.DmReply;
        if (!settings.Enabled || reply.Text.StartsWith(settings.IgnorePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var partner = await FindPartnerAsync(reply.ThreadId, cancellationToken).ConfigureAwait(false);
        if (partner is null)
        {
            await SendFeedbackAsync(reply, ReplyFeedbackKind.Refused, "This post isn't linked to a GeoGuessr chat partner.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await runner.RunAsync(
            reply.AuthorId,
            "dm-reply",
            partner.PartnerUserId,
            ct => SendAsync(reply, partner.PartnerUserId, ct),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var kind = result.Error!.Kind == ErrorKind.Unavailable ? ReplyFeedbackKind.NotSent : ReplyFeedbackKind.Refused;
            await SendFeedbackAsync(reply, kind, result.Error.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Result> SendAsync(ForumReply reply, string peerId, CancellationToken cancellationToken)
    {
        if (Validate(reply) is { } invalid)
        {
            return invalid;
        }

        if (!await WaitForSocketAsync(cancellationToken).ConfigureAwait(false))
        {
            return Unavailable();
        }

        // The pending row must exist before the send, because the echo can come back within milliseconds.
        var hash = ChatText.Hash(reply.Text);
        await SavePendingAsync(peerId, hash, reply, cancellationToken).ConfigureAwait(false);

        var result = await sender.SendDirectMessageAsync(peerId, reply.Text, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            await RemovePendingAsync(peerId, hash, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private Error? Validate(ForumReply reply)
    {
        var settings = options.Value.Control.DmReply;

        if (reply.HasOtherContent)
        {
            return new Error(ErrorKind.Validation, "dm-reply.unsupported", "GeoGuessr chat can only carry text, so attachments and stickers can't be sent.");
        }

        if (string.IsNullOrWhiteSpace(reply.Text))
        {
            return new Error(ErrorKind.Validation, "dm-reply.empty", "There is no text to send.");
        }

        if (reply.Text.Length > settings.MaxLength)
        {
            return new Error(ErrorKind.Validation, "dm-reply.too_long", $"GeoGuessr cuts messages at {settings.MaxLength} characters, and this one has {reply.Text.Length}.");
        }

        return TryTakeRateSlot(settings.MaxPerMinute)
            ? null
            : new Error(ErrorKind.Validation, "dm-reply.rate_limited", "Too many messages in the last minute. Try again shortly.");
    }

    private bool TryTakeRateSlot(int maxPerMinute)
    {
        var now = timeProvider.GetUtcNow();
        lock (_rateLock)
        {
            while (_recentSends.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1))
            {
                _recentSends.Dequeue();
            }

            if (_recentSends.Count >= maxPerMinute)
            {
                return false;
            }

            _recentSends.Enqueue(now);
            return true;
        }
    }

    private async Task<bool> WaitForSocketAsync(CancellationToken cancellationToken)
    {
        var state = connection.Current;
        return state.Phase switch
        {
            ConnectionPhase.Connected => true,
            ConnectionPhase.Reconnecting => await connection
                .WaitUntilConnectedAsync(options.Value.Control.DmReply.ReconnectGrace, cancellationToken)
                .ConfigureAwait(false),
            _ => false
        };
    }

    private Error Unavailable()
    {
        var retry = connection.NextAttemptAt is { } at
            ? $" The next attempt is <t:{at.ToUnixTimeSeconds()}:R>."
            : "";
        return new Error(ErrorKind.Unavailable, "dm-reply.offline", $"Not sent, because the bot isn't connected to GeoGuessr right now.{retry}");
    }

    private async Task<PartnerThread?> FindPartnerAsync(ulong threadId, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            return await store.FindPartnerThreadByThreadIdAsync(threadId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SavePendingAsync(string peerId, string hash, ForumReply reply, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            store.AddPendingReply(peerId, hash, reply.ThreadId, reply.MessageId, timeProvider.GetUtcNow());
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RemovePendingAsync(string peerId, string hash, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            if (await store.FindPendingReplyAsync(peerId, hash, cancellationToken).ConfigureAwait(false) is { } pending)
            {
                await store.RemovePendingReplyAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                await store.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendFeedbackAsync(ForumReply reply, ReplyFeedbackKind kind, string reason, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            store.Enqueue(new ReplyFeedback(reply.ThreadId, reply.MessageId, kind, reason));
            await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        outboxSignal.Wake();
    }
}
