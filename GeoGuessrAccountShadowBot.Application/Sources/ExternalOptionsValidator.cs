// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Sockets;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Application.Options;
using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Sources;

/// <summary>
/// Checks the API this instance serves and the sources it reads, at startup with the rest of the
/// configuration. Tokens are the only thing between the API and the account's data, so weak or
/// exposed ones are refused rather than warned about.
/// </summary>
public sealed class ExternalOptionsValidator : IValidateOptions<ShadowOptions>
{
    /// <summary>About 190 bits as base64, which a guess can't reach.</summary>
    public const int MinTokenLength = 32;

    private static readonly TimeSpan MinRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan MinAlertAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinUnhealthyAfter = TimeSpan.FromMinutes(1);

    public ValidateOptionsResult Validate(string? name, ShadowOptions options)
    {
        var errors = new List<string>();
        ValidateApi(options, errors);
        ValidateSources(options, errors);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateApi(ShadowOptions options, List<string> errors)
    {
        var api = options.ExternalApi;
        if (!api.Enabled)
        {
            return;
        }

        if (api.Port is < 1 or > 65535)
        {
            errors.Add("ExternalApi:Port must be between 1 and 65535.");
        }

        if (api.EventRetention < MinRetention || api.EventRetention > MaxRetention)
        {
            errors.Add($"ExternalApi:EventRetention must be between {MinRetention} and {MaxRetention.TotalDays} days.");
        }

        if (api.Clients.Count == 0)
        {
            errors.Add("ExternalApi:Clients is empty, so no instance could connect.");
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (client, settings) in api.Clients)
        {
            var path = $"ExternalApi:Clients:{client}";
            if (!AutomationRuleParser.IsValidName(client))
            {
                errors.Add($"{path}: the name must be lowercase letters, digits or dashes.");
            }

            if (CheckToken(settings.Token, MinTokenLength) is { } problem)
            {
                errors.Add($"{path}:Token {problem}");
            }
            else if (!tokens.TryAdd(settings.Token, client))
            {
                errors.Add($"{path}:Token is the same as the token of {tokens[settings.Token]}. Every client needs its own.");
            }

            foreach (var trigger in settings.Events)
            {
                if (ExternalEventFeed.Canonical(trigger) is not { } key)
                {
                    errors.Add($"{path}:Events: '{trigger}' isn't an event key or Custom:<name>.");
                }
                else if (EventKeys.All.Contains(key) && !options.IsEventEnabled(key))
                {
                    errors.Add($"{path}:Events: {key} is switched off in Events, so there would be nothing to send.");
                }
            }
        }
    }

    private static void ValidateSources(ShadowOptions options, List<string> errors)
    {
        foreach (var (source, settings) in options.Sources)
        {
            var path = $"Sources:{source}";
            if (!ExternalSourceNames.IsValid(source))
            {
                errors.Add($"{path}: the name must start with a lowercase letter and have only lowercase letters, digits or underscores, at most 32, because rules write it as sources.{source}.");
            }

            if (!string.Equals(settings.Type, SourceOptions.ShadowBotType, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}:Type must be {SourceOptions.ShadowBotType}, the only kind of source so far.");
            }

            if (CheckUrl(settings.Url) is { } urlProblem)
            {
                errors.Add($"{path}:Url {urlProblem}");
            }

            if (CheckToken(settings.Token, 1) is { } tokenProblem)
            {
                errors.Add($"{path}:Token {tokenProblem}");
            }

            if (settings.AlertAfter < MinAlertAfter)
            {
                errors.Add($"{path}:AlertAfter must be at least {MinAlertAfter}.");
            }

            if (settings.UnhealthyAfter < MinUnhealthyAfter)
            {
                errors.Add($"{path}:UnhealthyAfter must be at least {MinUnhealthyAfter}, or empty to never fail the health check.");
            }
        }
    }

    /// <summary>The token goes into a header, so nothing may end or split it.</summary>
    private static string? CheckToken(string token, int minLength)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "is required.";
        }

        if (token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c > '~'))
        {
            return "must be printable ASCII without spaces.";
        }

        return token.Length < minLength
            ? $"must be at least {minLength} characters. Generate one with: openssl rand -base64 32"
            : null;
    }

    /// <summary>
    /// The token travels with every request, so plain http is only allowed where the traffic stays
    /// on a private network, such as between containers.
    /// </summary>
    private static string? CheckUrl(Uri? url)
    {
        if (url is null)
        {
            return "is required.";
        }

        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            return "must be an absolute http or https address.";
        }

        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            return "must not hold a user name, query or fragment. The token goes in Token.";
        }

        return url.Scheme == "http" && !IsPrivateHost(url)
            ? "must use https, because the token would cross the internet readable. Plain http is only allowed for loopback, private addresses and single-label host names such as a Docker service name."
            : null;
    }

    public static bool IsPrivateHost(Uri url)
    {
        if (url.IsLoopback)
        {
            return true;
        }

        if (url.HostNameType == UriHostNameType.Dns)
        {
            return !url.IdnHost.Contains('.', StringComparison.Ordinal);
        }

        if (!IPAddress.TryParse(url.DnsSafeHost, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254)
               // Carrier-grade NAT space, which overlay networks such as Tailscale use.
               || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}

/// <summary>How sources are named, and how their events are named in rules.</summary>
public static partial class ExternalSourceNames
{
    public const string TriggerPrefix = "Source:";

    public const string PlayerSection = "player";

    public const string AccountSection = "account";

    /// <summary>What rules can read from another shadow bot, as <c>sources.name.section</c>.</summary>
    public static readonly IReadOnlySet<string> ShadowBotSections = new HashSet<string>(StringComparer.Ordinal) { PlayerSection, AccountSection };

    /// <summary>A source name is a Scriban identifier, because rules read it as <c>sources.name</c>.</summary>
    public static bool IsValid(string name) => NamePattern().IsMatch(name);

    /// <summary>The trigger of a source's event, such as <c>Source:dragon:ClubApplicationReceived</c>.</summary>
    public static string TriggerKey(string source, string trigger) => $"{TriggerPrefix}{source}:{trigger}";

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial System.Text.RegularExpressions.Regex NamePattern();
}
