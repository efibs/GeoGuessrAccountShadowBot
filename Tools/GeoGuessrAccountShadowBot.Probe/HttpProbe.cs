// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// Issues raw HTTP requests against the GeoGuessr API. Bodies go to the capture file, and the
/// console only shows their shape.
/// </summary>
internal sealed class HttpProbe : IDisposable
{
    private readonly HttpClient _client;

    public HttpProbe(ProbeSettings settings, bool allowWrite)
    {
        // Cookies are set by hand and redirects stay off, so the token can't follow a redirect elsewhere.
        var transport = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };

        _client = new HttpClient(new ReadOnlyGuardHandler(transport, settings.ApiBaseUrl, allowWrite))
        {
            BaseAddress = settings.ApiBaseUrl
        };
        _client.DefaultRequestHeaders.Add("Cookie", $"_ncfa={settings.NcfaToken}");
        _client.DefaultRequestHeaders.Add("X-Client", settings.ClientVersion);
        _client.DefaultRequestHeaders.Add("X-Locale", "en");
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(ProbeSettings.UserAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Requests the path, records the exchange, and prints the status and body shape.</summary>
    public async Task<string> SendAsync(HttpMethod method, string path, string? body, CaptureWriter capture, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        capture.Write("meta", $"{method} {path} -> {(int)response.StatusCode}");
        capture.Write("http", content);

        Console.WriteLine($"{method} {path} -> {(int)response.StatusCode} {response.StatusCode}, {content.Length} chars");
        PrintShape(content);
        return content;
    }

    /// <summary>Reads the id of the account the token belongs to.</summary>
    public async Task<string> ReadOwnUserIdAsync(CaptureWriter capture, CancellationToken cancellationToken)
    {
        var content = await SendAsync(HttpMethod.Get, "/api/v3/profiles", null, capture, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var user = root.TryGetProperty("user", out var nested) ? nested : root;
        return user.GetProperty("id").GetString() ?? throw new InvalidOperationException("The profile has no id.");
    }

    public void Dispose() => _client.Dispose();

    private static void PrintShape(string content)
    {
        try
        {
            Console.WriteLine($"  shape: {JsonShape.Describe(content)}");
        }
        catch (JsonException)
        {
            Console.WriteLine("  shape: not JSON");
        }
    }
}
