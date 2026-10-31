// SPDX-License-Identifier: GPL-2.0-or-later

using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Discord.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Discord.Hosting;

/// <summary>
/// Completes once the gateway is ready. <c>Ready</c> fires again after every reconnect, so
/// completing must tolerate being called twice.
/// </summary>
internal sealed class DiscordReadiness : IDiscordReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkReady() => _ready.TrySetResult();

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => _ready.Task.WaitAsync(cancellationToken);
}

/// <summary>
/// Runs the Discord connection. A rejected token stops the bot, because nothing can work without it.
/// </summary>
internal sealed partial class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    ForumReplyListener forumReplies,
    DiscordReadiness readiness,
    IServiceProvider services,
    IOptions<ShadowOptions> options,
    ILogger<DiscordBotService> logger) : IHostedService
{
    private int _modulesRegistered;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        client.Log += LogDiscordMessageAsync;
        interactions.Log += LogDiscordMessageAsync;
        client.Ready += OnReadyAsync;
        client.InteractionCreated += OnInteractionAsync;
        client.ThreadDeleted += OnThreadDeletedAsync;
        forumReplies.Attach();

        await client.LoginAsync(TokenType.Bot, options.Value.Discord.Token).ConfigureAwait(false);
        await client.StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        forumReplies.Detach();
        await client.StopAsync().ConfigureAwait(false);
    }

    private async Task OnReadyAsync()
    {
        readiness.MarkReady();

        if (Interlocked.Exchange(ref _modulesRegistered, 1) == 1)
        {
            return;
        }

        var control = options.Value.Control;
        if (control.FriendRequestButtons.Enabled)
        {
            await interactions.AddModuleAsync<FriendRequestModule>(services).ConfigureAwait(false);
        }

        if (control.ClubChat.Enabled)
        {
            await interactions.AddModuleAsync<ShadowCommandsModule>(services).ConfigureAwait(false);
        }

        await interactions.RegisterCommandsToGuildAsync(options.Value.Discord.GuildId).ConfigureAwait(false);
    }

    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(client, interaction);
        await interactions.ExecuteCommandAsync(context, services).ConfigureAwait(false);
    }

    /// <summary>A deleted post must lose its partner, so that the next message starts a new one.</summary>
    private async Task OnThreadDeletedAsync(Cacheable<SocketThreadChannel, ulong> thread)
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<IShadowStore>();
            await store.RemovePartnerThreadAsync(thread.Id).ConfigureAwait(false);
            await store.CommitAsync().ConfigureAwait(false);
        }
    }

    private Task LogDiscordMessageAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug
        };

        LogDiscord(level, message.Exception, message.Source, message.Message);
        return Task.CompletedTask;
    }

    [LoggerMessage(Message = "Discord {Source}: {Text}")]
    private partial void LogDiscord(LogLevel level, Exception? exception, string source, string? text);
}
