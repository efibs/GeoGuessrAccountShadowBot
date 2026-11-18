// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace GeoGuessrAccountShadowBot.Discord.Output;

/// <summary>
/// Ids of the buttons and forms of automations. Like friend request buttons, they carry everything
/// a click needs, so they keep working across restarts.
/// </summary>
internal static class AutomationButtonIds
{
    public const string CreateForm = "au:create";
    public const string EditFormPrefix = "au:edit:";
    public const string RuleField = "rule";

    public static string Approve(long runId) => $"au:ok:{runId.ToString(CultureInfo.InvariantCulture)}";

    public static string Reject(long runId) => $"au:no:{runId.ToString(CultureInfo.InvariantCulture)}";

    public static string EditForm(string ruleName) => EditFormPrefix + ruleName;
}
