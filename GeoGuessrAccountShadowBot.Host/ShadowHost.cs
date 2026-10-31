// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Discord;
using GeoGuessrAccountShadowBot.Host.Logging;
using GeoGuessrAccountShadowBot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace GeoGuessrAccountShadowBot.Host;

/// <summary>
/// Composes the bot. This lives apart from Program.cs so that a test can build the real object
/// graph, which is the only way to catch registrations that fail at runtime.
/// </summary>
internal static class ShadowHost
{
    private const string SecretsDirectory = "/run/secrets";

    /// <summary>Adds the sources on top of the host's defaults. Later sources win.</summary>
    public static void AddShadowConfiguration(this HostApplicationBuilder builder, string[] args)
    {
        builder.Configuration.AddKeyPerFile(SecretsDirectory, optional: true);
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

        // Added again so that they stay above Docker secrets and the local file.
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
    }

    public static void AddShadowBot(this HostApplicationBuilder builder)
    {
        var settings = builder.Configuration.Get<ShadowOptions>() ?? new ShadowOptions();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = RedactingConsoleFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<RedactingConsoleFormatter, ConsoleFormatterOptions>();
        builder.Services.AddSingleton(new SecretRedactor([settings.GeoGuessr.NcfaToken, settings.Discord.Token]));

        builder.Services.AddOptions<ShadowOptions>().Bind(builder.Configuration).ValidateOnStart();

        // Hosted services start in order and stop in reverse. The database has to be ready first,
        // and Discord has to stay up until everything that delivers to it has stopped.
        builder.Services.AddShadowPersistence(settings.Database.Path);
        builder.Services.AddShadowDiscord();
        builder.Services.AddShadowApplication();
        builder.Services.AddShadowGeoGuessr();
        builder.Services.AddHostedService<HealthFileWorker>();
    }
}
