// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Application.Options;

/// <summary>
/// The whole configuration, bound from the root so that one validator can check rules that span
/// sections, such as a control feature needing an event and a channel.
/// </summary>
public sealed class ShadowOptions
{
    public GeoGuessrOptions GeoGuessr { get; set; } = new();

    public PollingOptions Polling { get; set; } = new();

    public DiscordOptions Discord { get; set; } = new();

    public ForumOptions Forum { get; set; } = new();

    public ControlOptions Control { get; set; } = new();

    public Dictionary<string, EventOptions> Events { get; set; } = [];

    public DatabaseOptions Database { get; set; } = new();

    public bool IsEventEnabled(string key) => Events.TryGetValue(key, out var options) && options.Enabled;
}

public sealed class GeoGuessrOptions
{
    /// <summary>The bare value of the account's <c>_ncfa</c> cookie.</summary>
    public string NcfaToken { get; set; } = "";

    /// <summary>The site build to report. GeoGuessr may eventually close old clients with code 4200.</summary>
    public string ClientVersion { get; set; } = "web-1.8700-39ce1da";

    public Uri ApiBaseUrl { get; set; } = new("https://www.geoguessr.com");

    public Uri SocketUrl { get; set; } = new("wss://api.geoguessr.com/ws");

    public SocketOptions Socket { get; set; } = new();

    public YieldOptions Yield { get; set; } = new();

    public ClubOptions Club { get; set; } = new();
}

public sealed class SocketOptions
{
    /// <summary>When false, the bot runs on HTTP polling alone and can't send messages.</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Protocol ping interval and timeout. GeoGuessr answers pings, so this finds dead connections.</summary>
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(20);

    public int MaxFrameBytes { get; set; } = 4 * 1024 * 1024;

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a connection must last before the backoff starts over.</summary>
    public TimeSpan StableAfter { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class YieldOptions
{
    /// <summary>How long to stay off the socket after being superseded by another connection.</summary>
    public TimeSpan Initial { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan Max { get; set; } = TimeSpan.FromMinutes(60);
}

public sealed class ClubOptions
{
    public bool Enabled { get; set; }

    /// <summary>The club to follow. When empty, the account's own club is looked up.</summary>
    public string? ClubId { get; set; }
}

public sealed class PollingOptions
{
    /// <summary>The catch-up interval while the socket is down.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The catch-up interval while connected, which finds anything the socket missed.</summary>
    public TimeSpan ConnectedSafetyInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a doorbell frame waits, so that a burst of them causes one sync.</summary>
    public TimeSpan DoorbellDebounce { get; set; } = TimeSpan.FromSeconds(2);

    public int NotificationPageSize { get; set; } = 20;

    public int MaxNotificationPages { get; set; } = 10;

    /// <summary>Items older than this are recorded without notifying.</summary>
    public TimeSpan MaxCatchUpAge { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan RankedInterval { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class DiscordOptions
{
    public string Token { get; set; } = "";

    public ulong GuildId { get; set; }

    /// <summary>Where events go unless they have their own <see cref="EventOptions.ChannelId"/>.</summary>
    public ulong DefaultChannelId { get; set; }

    /// <summary>Where connection status goes. Defaults to the default channel.</summary>
    public ulong? StatusChannelId { get; set; }

    public List<ulong> StatusPingRoleIds { get; set; } = [];

    /// <summary>
    /// Confirms the Message Content intent is enabled in the Discord developer portal. Requesting it
    /// without the portal toggle makes Discord refuse the whole gateway connection.
    /// </summary>
    public bool MessageContentIntent { get; set; }
}

public sealed class ForumOptions
{
    /// <summary>The forum channel that holds one post per chat partner.</summary>
    public ulong? ChannelId { get; set; }
}

public sealed class ControlOptions
{
    /// <summary>Roles allowed to act as the account. An empty list allows nobody.</summary>
    public List<ulong> AuthorizedRoleIds { get; set; } = [];

    public DmReplyOptions DmReply { get; set; } = new();

    public ToggleOptions FriendRequestButtons { get; set; } = new();

    public ClubChatOptions ClubChat { get; set; } = new();
}

public sealed class DmReplyOptions
{
    public bool Enabled { get; set; }

    /// <summary>Messages starting with this stay in Discord, so staff can talk in a post.</summary>
    public string IgnorePrefix { get; set; } = "//";

    /// <summary>GeoGuessr silently cuts messages at 300 characters, so longer ones are refused.</summary>
    public int MaxLength { get; set; } = 300;

    public int MaxPerMinute { get; set; } = 20;

    /// <summary>How long to wait for a sent message to come back on the socket.</summary>
    public TimeSpan EchoTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a reply waits for a socket that is reconnecting.</summary>
    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(20);
}

public sealed class ClubChatOptions
{
    public bool Enabled { get; set; }

    public int MaxLength { get; set; } = 300;
}

public sealed class ToggleOptions
{
    public bool Enabled { get; set; }
}

public sealed class EventOptions
{
    public bool Enabled { get; set; }

    /// <summary>Overrides <see cref="DiscordOptions.DefaultChannelId"/> for this event.</summary>
    public ulong? ChannelId { get; set; }

    public List<ulong> PingRoleIds { get; set; } = [];
}

public sealed class DatabaseOptions
{
    public string Path { get; set; } = "/data/shadow.db";
}
