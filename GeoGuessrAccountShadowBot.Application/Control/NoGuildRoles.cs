// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Ports;

namespace GeoGuessrAccountShadowBot.Application.Control;

/// <summary>The roles of a headless bot, where there is no guild, so nobody holds a role.</summary>
public sealed class NoGuildRoles : IGuildRoleReader
{
    public Task<IReadOnlyCollection<ulong>> GetRoleIdsAsync(ulong discordUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<ulong>>([]);
}
