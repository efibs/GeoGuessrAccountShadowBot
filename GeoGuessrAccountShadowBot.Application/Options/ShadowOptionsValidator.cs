// SPDX-License-Identifier: GPL-2.0-or-later

using GeoGuessrAccountShadowBot.Domain.Events;
using Microsoft.Extensions.Options;

namespace GeoGuessrAccountShadowBot.Application.Options;

/// <summary>
/// Checks the configuration at startup and reports every problem at once, so a bad deployment
/// doesn't need one restart per mistake.
/// </summary>
public sealed class ShadowOptionsValidator : IValidateOptions<ShadowOptions>
{
    private const string AllowedHostSuffix = "geoguessr.com";
    private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumRankedInterval = TimeSpan.FromMinutes(5);

    public ValidateOptionsResult Validate(string? name, ShadowOptions options)
    {
        var errors = new List<string>();

        ValidateGeoGuessr(options.GeoGuessr, errors);
        ValidateEvents(options, errors);
        ValidateControl(options, errors);
        ValidatePolling(options.Polling, errors);

        if (string.IsNullOrWhiteSpace(options.Discord.Token))
        {
            errors.Add("Discord:Token is required.");
        }

        if (options.Discord.GuildId == 0)
        {
            errors.Add("Discord:GuildId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Database.Path))
        {
            errors.Add("Database:Path is required.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateGeoGuessr(GeoGuessrOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.NcfaToken))
        {
            errors.Add("GeoGuessr:NcfaToken is required.");
        }
        else if (options.NcfaToken.StartsWith("_ncfa=", StringComparison.Ordinal)
                 || options.NcfaToken.Any(c => c == ';' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            // The value goes straight into a Cookie header, so nothing may end the cookie early.
            errors.Add("GeoGuessr:NcfaToken must be the bare cookie value, without the cookie name or separators.");
        }

        if (!IsGeoGuessrHost(options.ApiBaseUrl, "https"))
        {
            errors.Add("GeoGuessr:ApiBaseUrl must be an https geoguessr.com address, because the session cookie goes there.");
        }

        if (!IsGeoGuessrHost(options.SocketUrl, "wss"))
        {
            errors.Add("GeoGuessr:SocketUrl must be a wss geoguessr.com address, because the session cookie goes there.");
        }

        if (options.Yield.Initial > options.Yield.Max)
        {
            errors.Add("GeoGuessr:Yield:Initial must not exceed GeoGuessr:Yield:Max.");
        }
    }

    private static void ValidateEvents(ShadowOptions options, List<string> errors)
    {
        foreach (var key in options.Events.Keys.Where(key => !EventKeys.All.Contains(key)))
        {
            errors.Add($"Events:{key} is not a known event.");
        }

        foreach (var key in EventKeys.All.Where(key => !options.Events.ContainsKey(key)))
        {
            errors.Add($"Events:{key} is missing.");
        }

        foreach (var (key, settings) in options.Events.Where(entry => entry.Value.Enabled))
        {
            if (key == EventKeys.DmMessage)
            {
                continue;
            }

            if (settings.ChannelId is null && options.Discord.DefaultChannelId == 0)
            {
                errors.Add($"Events:{key} needs a ChannelId, or Discord:DefaultChannelId must be set.");
            }
        }

        if (options.IsEventEnabled(EventKeys.DmMessage) && options.Forum.ChannelId is null)
        {
            errors.Add($"Events:{EventKeys.DmMessage} needs Forum:ChannelId, because each chat partner gets a forum post.");
        }

        var clubEvents = new[]
        {
            EventKeys.ClubChat, EventKeys.ClubMemberJoined, EventKeys.ClubMemberLeft,
            EventKeys.ClubRoleChanged, EventKeys.ClubMissionBoard
        };
        foreach (var key in clubEvents.Where(key => options.IsEventEnabled(key) && !options.GeoGuessr.Club.Enabled))
        {
            errors.Add($"Events:{key} needs GeoGuessr:Club:Enabled.");
        }
    }

    private static void ValidateControl(ShadowOptions options, List<string> errors)
    {
        var control = options.Control;
        var anyEnabled = control.DmReply.Enabled || control.FriendRequestButtons.Enabled || control.ClubChat.Enabled;

        if (anyEnabled && control.AuthorizedRoleIds.Count == 0)
        {
            errors.Add("Control:AuthorizedRoleIds must not be empty while a control feature is enabled.");
        }

        if (control.AuthorizedRoleIds.Contains(options.Discord.GuildId))
        {
            errors.Add("Control:AuthorizedRoleIds must not contain the guild id, which is the @everyone role.");
        }

        if (control.DmReply.Enabled)
        {
            if (!options.IsEventEnabled(EventKeys.DmMessage))
            {
                errors.Add($"Control:DmReply needs Events:{EventKeys.DmMessage}.");
            }

            if (options.Forum.ChannelId is null)
            {
                errors.Add("Control:DmReply needs Forum:ChannelId.");
            }

            if (!options.Discord.MessageContentIntent)
            {
                errors.Add("Control:DmReply needs Discord:MessageContentIntent, after enabling the Message Content intent in the Discord developer portal.");
            }

            if (!options.GeoGuessr.Socket.Enabled)
            {
                errors.Add("Control:DmReply needs GeoGuessr:Socket:Enabled, because messages are sent over the socket.");
            }
        }

        if (control.FriendRequestButtons.Enabled && !options.IsEventEnabled(EventKeys.FriendRequestReceived))
        {
            errors.Add($"Control:FriendRequestButtons needs Events:{EventKeys.FriendRequestReceived}.");
        }

        if (control.ClubChat.Enabled && (!options.GeoGuessr.Club.Enabled || !options.GeoGuessr.Socket.Enabled))
        {
            errors.Add("Control:ClubChat needs GeoGuessr:Club:Enabled and GeoGuessr:Socket:Enabled.");
        }
    }

    private static void ValidatePolling(PollingOptions options, List<string> errors)
    {
        if (options.Interval < MinimumPollingInterval)
        {
            errors.Add($"Polling:Interval must be at least {MinimumPollingInterval}.");
        }

        if (options.ConnectedSafetyInterval < MinimumPollingInterval)
        {
            errors.Add($"Polling:ConnectedSafetyInterval must be at least {MinimumPollingInterval}.");
        }

        if (options.RankedInterval < MinimumRankedInterval)
        {
            errors.Add($"Polling:RankedInterval must be at least {MinimumRankedInterval}.");
        }
    }

    private static bool IsGeoGuessrHost(Uri url, string scheme) =>
        url.Scheme == scheme
        && (url.Host == AllowedHostSuffix || url.Host.EndsWith($".{AllowedHostSuffix}", StringComparison.OrdinalIgnoreCase));
}
