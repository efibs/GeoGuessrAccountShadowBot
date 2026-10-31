// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// Removes the token from anything the probe prints, in both its raw and URL-encoded forms.
/// </summary>
internal sealed class TokenRedactor(string token)
{
    private const string Replacement = "<redacted>";

    public string Redact(string text) => text
        .Replace(token, Replacement, StringComparison.Ordinal)
        .Replace(WebUtility.UrlEncode(token), Replacement, StringComparison.Ordinal)
        .Replace(WebUtility.UrlDecode(token), Replacement, StringComparison.Ordinal);
}
