// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Discord.Output;

/// <summary>
/// Button ids hold everything a click needs, so that buttons keep working across restarts.
/// </summary>
internal static class FriendRequestButtonIds
{
    public static string Accept(string userId) => $"fr:a:{userId}";

    public static string Decline(string userId) => $"fr:d:{userId}";

    public static string ConfirmDecline(string userId) => $"fr:c:{userId}";
}
