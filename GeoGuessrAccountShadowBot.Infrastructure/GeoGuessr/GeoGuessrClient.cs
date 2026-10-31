// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr;

/// <summary>
/// How the bot identifies itself to GeoGuessr: honestly, rather than as a browser.
/// </summary>
internal static class GeoGuessrClient
{
    public const string UserAgent = "GeoGuessrAccountShadowBot (+https://github.com/efibs/GeoGuessrAccountShadowBot)";

    public const string Origin = "https://www.geoguessr.com";
}
