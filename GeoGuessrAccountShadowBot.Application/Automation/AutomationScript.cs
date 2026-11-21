// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace GeoGuessrAccountShadowBot.Application.Automation;

/// <summary>A condition or template failed while it ran, for example on a type mismatch.</summary>
public sealed class AutomationScriptException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// A parsed Scriban condition or template. Scriban only sees the variables the bot puts into a
/// <see cref="AutomationScope"/>, never .NET objects, and runs with limits on loops, recursion,
/// output and regex time, so a rule can't reach anything or hang the bot.
/// </summary>
public sealed partial class AutomationScript
{
    private AutomationScript(string source, Template template, bool isExpression)
    {
        Source = source;
        Template = template;
        IsExpression = isExpression;
    }

    public string Source { get; }

    public bool IsExpression { get; }

    internal Template Template { get; }

    /// <summary>
    /// Whether the script reads more of the player than the bot knows without asking GeoGuessr,
    /// which costs a profile lookup. Anything other than a plain access to a cheap field counts.
    /// </summary>
    public bool ReadsPlayer { get; private init; }

    /// <summary>The sections of sources the script reads, which are fetched before it runs.</summary>
    public IReadOnlyList<SourceRead> SourceReads { get; private init; } = [];

    /// <summary>Parses text with <c>{{ }}</c> blocks, such as a message.</summary>
    public static AutomationScript? ParseTemplate(string source, out string? error) => Parse(source, isExpression: false, out error);

    /// <summary>Parses a bare expression, such as <c>player.level &gt;= 20</c>.</summary>
    public static AutomationScript? ParseExpression(string source, out string? error) => Parse(source, isExpression: true, out error);

    private static AutomationScript? Parse(string source, bool isExpression, out string? error)
    {
        var lexer = isExpression ? new LexerOptions { Mode = ScriptMode.ScriptOnly } : LexerOptions.Default;
        var template = Template.Parse(source, lexerOptions: lexer);
        if (template.HasErrors)
        {
            error = string.Join("; ", template.Messages.Select(message => message.Message));
            return null;
        }

        error = null;
        return new AutomationScript(source, template, isExpression) { ReadsPlayer = NeedsProfile(source), SourceReads = FindSourceReads(source) };
    }

    private static readonly HashSet<string> CheapPlayerFields = new(StringComparer.Ordinal)
    {
        "id", "nick", "profile_url", "is_friend", "has_requested", "was_requested", "is_club_member", "has_applied"
    };

    private static bool NeedsProfile(string source) =>
        PlayerReference().Matches(source).Any(match => !match.Groups[1].Success || !CheapPlayerFields.Contains(match.Groups[1].Value));

    /// <summary>
    /// Finds <c>sources.name.section</c>. A reference without a section reads all of them, and one
    /// without a name reads every source, since the script could reach any of them.
    /// </summary>
    private static SourceRead[] FindSourceReads(string source) =>
        SourceReference().Matches(source)
            .Select(match => new SourceRead(
                match.Groups[1].Success ? match.Groups[1].Value : null,
                match.Groups[2].Success ? match.Groups[2].Value : null))
            .Distinct()
            .ToArray();

    // A player read through a source, such as sources.dragon.player.level, isn't this account's.
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\.\s*)\bplayer\b(?:\s*\.\s*(\w+))?")]
    private static partial System.Text.RegularExpressions.Regex PlayerReference();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\.\s*)\bsources\b(?:\s*\.\s*(\w+)(?:\s*\.\s*(\w+))?)?")]
    private static partial System.Text.RegularExpressions.Regex SourceReference();
}

/// <summary>A part of a source a script reads.</summary>
/// <param name="Source">The source, or null if the script could reach any.</param>
/// <param name="Section">The section, such as <c>player</c>, or null if it could reach any.</param>
public sealed record SourceRead(string? Source, string? Section);

/// <summary>
/// The variables one evaluation sees. A scope is cheap, and made again for every evaluation of a
/// run, so nothing a script does can leak into the next one.
/// </summary>
public sealed class AutomationScope(ScriptObject globals)
{
    private const int LoopLimit = 1000;
    private const int RecursiveLimit = 20;
    private const int StringLimit = 4000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Evaluates a condition. Like Scriban, only null and false are false.</summary>
    public bool Evaluate(AutomationScript condition)
    {
        var (context, timeout) = CreateContext(marking: false);
        using (timeout)
        {
            var value = Run(() => condition.Template.Evaluate(context));
            return value switch
            {
                null => false,
                bool flag => flag,
                _ => true
            };
        }
    }

    /// <summary>Renders text for GeoGuessr, as plain text.</summary>
    public string RenderPlain(AutomationScript template) => AutomationMarkup.Strip(Render(template, marking: false)).Trim();

    /// <summary>Renders text for Discord, with every inserted value marked as untrusted.</summary>
    public string RenderMarked(AutomationScript template) => Render(template, marking: true).Trim();

    private string Render(AutomationScript template, bool marking)
    {
        var (context, timeout) = CreateContext(marking);
        using (timeout)
        {
            return template.IsExpression
                ? context.ObjectToString(Run(() => template.Template.Evaluate(context))) ?? ""
                : Run(() => template.Template.Render(context)) ?? "";
        }
    }

    private static T Run<T>(Func<T> evaluation)
    {
        try
        {
            return evaluation();
        }
        catch (ScriptRuntimeException exception)
        {
            throw new AutomationScriptException(exception.OriginalMessage, exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new AutomationScriptException("The script took too long.", exception);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException exception)
        {
            throw new AutomationScriptException("A regular expression took too long.", exception);
        }
    }

    private (TemplateContext Context, CancellationTokenSource Timeout) CreateContext(bool marking)
    {
        var timeout = new CancellationTokenSource(EvaluationTimeout);
        var context = marking ? new MarkingTemplateContext() : new TemplateContext();
        context.LoopLimit = LoopLimit;
        context.RecursiveLimit = RecursiveLimit;
        context.LimitToString = StringLimit;
        context.OutputLimit = StringLimit * 2;
        context.RegexTimeOut = RegexTimeout;
        context.StrictVariables = false;
        context.EnableRelaxedMemberAccess = true;
        context.EnableRelaxedTargetAccess = true;
        context.TemplateLoader = null;
        context.CancellationToken = timeout.Token;
        context.PushCulture(CultureInfo.InvariantCulture);
        context.PushGlobal(globals);
        return (context, timeout);
    }

    /// <summary>Wraps every value written into the output in markers.</summary>
    private sealed class MarkingTemplateContext : TemplateContext
    {
        public override string ObjectToString(object? value, bool nested = false)
        {
            var text = base.ObjectToString(value, nested) ?? "";
            return nested || value is null ? text : AutomationMarkup.Untrusted(text);
        }
    }
}
