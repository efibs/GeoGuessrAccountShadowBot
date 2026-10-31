// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Host;
using Microsoft.Extensions.Hosting;

var builder = ShadowHost.CreateBuilder(args);
builder.AddShadowBot();

try
{
    await builder.Build().RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    // The host has already logged this, through the redacting formatter.
    return 1;
}
