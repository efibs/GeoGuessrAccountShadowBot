// SPDX-License-Identifier: GPL-2.0-or-later

using Discord;

namespace GeoGuessrAccountShadowBot.Discord.Output;

/// <summary>
/// The only place that decides who a message may ping. Each call makes a new instance, because
/// Discord.Net's shared <c>AllowedMentions.None</c> is mutable and must never be changed.
/// </summary>
internal static class MentionPolicy
{
    public static AllowedMentions Nobody() => new(AllowedMentionTypes.None);

    /// <summary>Allows exactly the given roles, and nothing that happens to appear in the text.</summary>
    public static AllowedMentions Roles(IReadOnlyCollection<ulong> roleIds) => roleIds.Count == 0
        ? Nobody()
        : new AllowedMentions { AllowedTypes = null, RoleIds = [.. roleIds], UserIds = [] };

    public static string? PingLine(IReadOnlyCollection<ulong> roleIds) =>
        roleIds.Count == 0 ? null : string.Join(' ', roleIds.Select(id => $"<@&{id}>"));
}
