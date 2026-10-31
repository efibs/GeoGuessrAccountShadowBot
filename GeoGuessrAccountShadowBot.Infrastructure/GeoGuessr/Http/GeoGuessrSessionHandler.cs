// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Options;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Infrastructure.GeoGuessr.Http;

/// <summary>
/// Attaches the session to every request, but only for the configured host, so that the cookie
/// can't leave for anywhere else.
/// </summary>
internal sealed class GeoGuessrSessionHandler(IOptions<ShadowOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var settings = options.Value.GeoGuessr;
        if (request.RequestUri?.Host != settings.ApiBaseUrl.Host)
        {
            throw new InvalidOperationException($"Refusing to send the session to {request.RequestUri?.Host}.");
        }

        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", $"_ncfa={settings.NcfaToken}");
        request.Headers.TryAddWithoutValidation("X-Client", settings.ClientVersion);
        request.Headers.TryAddWithoutValidation("X-Locale", "en");
        request.Headers.UserAgent.ParseAdd(GeoGuessrClient.UserAgent);

        return base.SendAsync(request, cancellationToken);
    }
}
