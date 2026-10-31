// SPDX-License-Identifier: GPL-2.0-or-later

using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using GeoGuessrAccountShadowBot.Discord.Hosting;
using GeoGuessrAccountShadowBot.Discord.Input;
using GeoGuessrAccountShadowBot.Discord.Output;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Discord;

/// <summary>
/// Registers the Discord layer.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddShadowDiscord(this IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var discord = provider.GetRequiredService<IOptions<ShadowOptions>>().Value.Discord;

            // Message Content is privileged, so it's only requested when the operator confirmed it.
            var intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages;
            if (discord.MessageContentIntent)
            {
                intents |= GatewayIntents.MessageContent;
            }

            return new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = intents });
        });

        services.AddSingleton(provider => new InteractionService(provider.GetRequiredService<DiscordSocketClient>()));
        services.AddSingleton<DiscordReadiness>();
        services.AddSingleton<IDiscordReadiness>(provider => provider.GetRequiredService<DiscordReadiness>());
        services.AddSingleton<IGuildRoleReader, GuildRoleReader>();
        services.AddSingleton<ForumReplyListener>();
        services.AddScoped<IDiscordPublisher, DiscordPublisher>();
        services.AddHostedService<DiscordBotService>();
        return services;
    }
}
