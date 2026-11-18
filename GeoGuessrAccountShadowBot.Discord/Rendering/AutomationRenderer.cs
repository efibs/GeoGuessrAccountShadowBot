// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Notifications;
using GeoGuessrAccountShadowBot.Domain.Model;

namespace GeoGuessrAccountShadowBot.Discord.Rendering;

/// <summary>
/// Renders automation text and cards. Rule authors may write Discord markdown, so their text stays
/// as written, while every value marked by <see cref="AutomationMarkup"/> is escaped like any other
/// text from GeoGuessr.
/// </summary>
public static class AutomationRenderer
{
    private const int MaxDescriptionLength = 3900;

    private const uint InfoColor = 0x5865F2;
    private const uint SuccessColor = 0x57F287;
    private const uint WarningColor = 0xFEE75C;
    private const uint DangerColor = 0xED4245;
    private const uint DryRunColor = 0x99AAB5;

    /// <summary>Turns marked text into Discord text, escaping the marked parts.</summary>
    public static string ToDiscord(string marked)
    {
        var result = new StringBuilder(marked.Length + 16);
        var segment = new StringBuilder();
        var depth = 0;

        foreach (var character in marked)
        {
            switch (character)
            {
                case AutomationMarkup.Open:
                    if (depth == 0)
                    {
                        result.Append(UntrustedMarkdown.Clean(segment.ToString()));
                        segment.Clear();
                    }

                    depth++;
                    break;
                case AutomationMarkup.Close when depth > 0:
                    depth--;
                    if (depth == 0)
                    {
                        result.Append(UntrustedMarkdown.Escape(segment.ToString()));
                        segment.Clear();
                    }

                    break;
                case AutomationMarkup.Close:
                    break;
                default:
                    segment.Append(character);
                    break;
            }
        }

        // An unclosed marker can only come from a bug, and then the rest is treated as untrusted.
        result.Append(depth > 0 ? UntrustedMarkdown.Escape(segment.ToString()) : UntrustedMarkdown.Clean(segment.ToString()));
        return result.ToString();
    }

    public static RenderedCard RenderPost(AutomationPost post) => new(
        post.Title is { } title ? Limit(ToDiscord(title), 256) : "Automation",
        Truncate(ToDiscord(post.Text)),
        post.Style switch
        {
            CardStyle.Success => SuccessColor,
            CardStyle.Warning => WarningColor,
            CardStyle.Danger => DangerColor,
            _ => InfoColor
        },
        null)
    {
        Author = $"Automation {post.RuleName}"
    };

    public static string RenderForumNote(AutomationForumNote note) =>
        $"🤖 **Automation {note.RuleName}**\n{ToDiscord(note.Text)}";

    public static RenderedCard RenderReport(AutomationReport report, UntrustedText? playerNick)
    {
        var lines = new List<string> { UntrustedMarkdown.Escape(report.Outcome) };
        if (playerNick is { } nick)
        {
            lines.Insert(0, $"**Player:** {UntrustedMarkdown.Escape(nick)}");
        }

        if (report.Lines.Count > 0)
        {
            lines.Add("");
            lines.AddRange(report.Lines.Select(ToDiscord));
        }

        var title = report.DryRun ? $"Dry run of {report.RuleName}" : $"{report.RuleName}: {Describe(report.State)}";
        var color = report.DryRun ? DryRunColor : report.State switch
        {
            AutomationRunState.Completed => SuccessColor,
            AutomationRunState.Failed or AutomationRunState.Expired => DangerColor,
            AutomationRunState.Rejected or AutomationRunState.Cancelled => WarningColor,
            _ => InfoColor
        };

        return new RenderedCard(title, Truncate(string.Join('\n', lines)), color, null) { Author = $"Automation run #{report.RunId}" };
    }

    public static RenderedCard RenderApproval(AutomationApprovalRequest request, UntrustedText? playerNick)
    {
        var lines = new List<string>();
        if (playerNick is { } nick)
        {
            lines.Add($"**Player:** {UntrustedMarkdown.Escape(nick)}");
        }

        if (request.Prompt is { } prompt)
        {
            lines.Add(ToDiscord(prompt));
        }

        if (request.Planned.Count > 0)
        {
            lines.Add("");
            lines.Add("**If approved:**");
            lines.AddRange(request.Planned.Select(line => $"- {ToDiscord(line)}"));
        }

        lines.Add("");
        lines.Add($"Ends <t:{request.ExpiresAt.ToUnixTimeSeconds()}:R> without an answer.");

        return new RenderedCard($"Approve {request.RuleName}?", Truncate(string.Join('\n', lines)), WarningColor, null)
        {
            Author = $"Automation run #{request.RunId}"
        };
    }

    /// <summary>The line that replaces the buttons once an approval is answered.</summary>
    public static string DescribeDecision(AutomationApprovalOutcome outcome) => outcome.State switch
    {
        AutomationRunState.Pending => $"✅ Approved by <@{outcome.DecidedBy}>",
        AutomationRunState.Rejected => $"🚫 Rejected by <@{outcome.DecidedBy}>",
        AutomationRunState.Expired => "⌛ Nobody answered in time",
        _ => "✖️ Cancelled"
    };

    public static string Describe(AutomationRunState state) => state switch
    {
        AutomationRunState.Completed => "done",
        AutomationRunState.Skipped => "skipped",
        AutomationRunState.Failed => "failed",
        AutomationRunState.Cancelled => "cancelled",
        AutomationRunState.Rejected => "rejected",
        AutomationRunState.Expired => "expired",
        _ => state.ToString()
    };

    private static string Limit(string text, int length) => text.Length <= length ? text : $"{MessageChunker.Chunk(text, length - 1)[0]}…";

    private static string Truncate(string text) => Limit(text, MaxDescriptionLength);
}
