// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// The settings of a probe run, read from <c>appsettings.Local.json</c> next to the executable.
/// The <c>GEOGUESSR_NCFA</c> environment variable overrides the token.
/// </summary>
internal sealed record ProbeSettings(string NcfaToken, string ClientVersion, Uri ApiBaseUrl, Uri SocketUrl)
{
    private const string SettingsFileName = "appsettings.Local.json";
    private const string TokenEnvironmentVariable = "GEOGUESSR_NCFA";
    private const string DefaultClientVersion = "web-1.8700-39ce1da";

    /// <summary>Identifies the probe honestly rather than pretending to be a browser.</summary>
    public const string UserAgent = "GeoGuessrAccountShadowBot-Probe/0.1 (+https://github.com/efibs/GeoGuessrAccountShadowBot)";

    public static ProbeSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        using var document = File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : JsonDocument.Parse("{}");
        var root = document.RootElement;

        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable) ?? ReadString(root, "NcfaToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"No token found. Set \"NcfaToken\" in {SettingsFileName} or the {TokenEnvironmentVariable} environment variable.");
        }

        // The value goes straight into a Cookie header, so anything that could end the cookie must be refused.
        if (token.StartsWith("_ncfa=", StringComparison.Ordinal) || token.Any(c => c == ';' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new InvalidOperationException("NcfaToken must be the bare cookie value, without the \"_ncfa=\" name or any separators.");
        }

        return new ProbeSettings(
            token,
            ReadString(root, "ClientVersion") ?? DefaultClientVersion,
            new Uri(ReadString(root, "ApiBaseUrl") ?? "https://www.geoguessr.com"),
            new Uri(ReadString(root, "SocketUrl") ?? "wss://api.geoguessr.com/ws"));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value.GetString() : null;
}
