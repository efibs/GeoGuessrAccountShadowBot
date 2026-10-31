// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Text.RegularExpressions;

namespace GeoGuessrAccountShadowBot.Host.Logging;

/// <summary>
/// Removes credentials from log text. It matches the real values, and also anything that looks like
/// a session cookie, so that a value from an old configuration can't get through.
/// </summary>
public sealed partial class SecretRedactor
{
    private const string Replacement = "<redacted>";

    private readonly string[] _secrets;

    public SecretRedactor(IEnumerable<string?> secrets) =>
        _secrets = secrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .SelectMany(secret => new[] { secret!, WebUtility.UrlEncode(secret!), WebUtility.UrlDecode(secret!) })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public string Redact(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Replacement, StringComparison.Ordinal);
        }

        return CookieRegex().Replace(text, $"_ncfa={Replacement}");
    }

    [GeneratedRegex(@"_ncfa=[^\s;""']+")]
    private static partial Regex CookieRegex();
}
