// SPDX-License-Identifier: GPL-2.0-or-later

using Discord.WebSocket;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Application.Ports;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Discord.Output;

/// <summary>
/// Reads roles over REST. The gateway cache keeps a known user's old roles, and a role that was
/// just taken away must stop working immediately.
/// </summary>
internal sealed class GuildRoleReader(DiscordSocketClient client, IOptions<ShadowOptions> options) : IGuildRoleReader
{
    public async Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong discordUserId, CancellationToken cancellationToken = default)
    {
        var member = await client.Rest.GetGuildUserAsync(options.Value.Discord.GuildId, discordUserId).ConfigureAwait(false);
        return member?.RoleIds ?? [];
    }
}
