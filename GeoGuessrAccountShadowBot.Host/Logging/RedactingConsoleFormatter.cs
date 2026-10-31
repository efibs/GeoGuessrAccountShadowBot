// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Host.Logging;

/// <summary>
/// A plain console format with redaction. It's applied to the finished line, so messages,
/// exceptions and anything Discord.Net logs are all covered.
/// </summary>
internal sealed class RedactingConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> options, SecretRedactor redactor, TimeProvider timeProvider)
    : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "redacting";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var time = options.CurrentValue.UseUtcTimestamp ? timeProvider.GetUtcNow() : timeProvider.GetLocalNow();
        var line = $"{time:yyyy-MM-dd HH:mm:ss} {logEntry.LogLevel,-11} {logEntry.Category}: {message}";

        if (logEntry.Exception is not null)
        {
            line += $"{Environment.NewLine}{logEntry.Exception}";
        }

        textWriter.WriteLine(redactor.Redact(line));
    }
}
