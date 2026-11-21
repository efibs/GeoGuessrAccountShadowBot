// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Notifications;

/// <summary>
/// Decides what goes into the outbox. With Discord, everything does. Headless, nothing does, since
/// nothing would ever deliver it: notes to the operators and automation reports go to the log
/// instead, and the rest, such as cards and chat lines, is dropped.
/// </summary>
public sealed partial class OutboxFilter(IOptions<ShadowOptions> options, ILogger<OutboxFilter> logger)
{
    public bool Accepts(OutboxMessage message)
    {
        if (!options.Value.IsHeadless)
        {
            return true;
        }

        switch (message)
        {
            case StatusMessage status:
                LogStatus(status.Title, status.Detail);
                break;
            case AutomationReport report:
                // The outcome is written by the bot, but may quote a rendered value, so marks go.
                LogReport(report.RuleName, report.RunId, report.State, AutomationMarkup.Strip(report.Outcome));
                break;
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Title}: {Detail}")]
    private partial void LogStatus(string title, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automation report: {Rule} run {RunId} ended as {State}: {Outcome}")]
    private partial void LogReport(string rule, long runId, AutomationRunState state, string outcome);
}
