// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// Refuses every HTTP request that could change the account unless writes were explicitly allowed,
/// and refuses to send the session cookie to any host but the configured API host.
/// </summary>
internal sealed class ReadOnlyGuardHandler(HttpMessageHandler innerHandler, Uri apiBaseUrl, bool allowWrite)
    : DelegatingHandler(innerHandler)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != apiBaseUrl.Host)
        {
            throw new InvalidOperationException($"Refusing to send a request to {request.RequestUri?.Host}.");
        }

        if (!allowWrite && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            throw new InvalidOperationException($"Refusing {request.Method}: pass --allow-write to change the account.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
