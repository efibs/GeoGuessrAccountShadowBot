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

    /// <summary>The API other instances read this one through, as an external source.</summary>
    public ExternalApiOptions ExternalApi { get; set; } = new();

    /// <summary>Other systems this instance reads events and data from, by the name rules use.</summary>
    public Dictionary<string, SourceOptions> Sources { get; set; } = [];

    /// <summary>Whether the bot runs without Discord.</summary>
    public bool IsHeadless => !Discord.Enabled;

    public bool IsEventEnabled(string key) => Events.TryGetValue(key, out var options) && options.Enabled;

    /// <summary>Whether the event is on and posts its own card, rather than only starting rules.</summary>
    public bool IsCardEnabled(string key) => Events.TryGetValue(key, out var options) && options.Enabled && options.Card;
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

    /// <summary>How often pending club applications are read. GeoGuessr doesn't push them.</summary>
    public TimeSpan ClubApplicationsInterval { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class DiscordOptions
{
    /// <summary>
    /// Off runs the bot headless: it watches the account, runs configured rules and serves other
    /// instances, but starts no Discord bot and needs no token.
    /// </summary>
    public bool Enabled { get; set; } = true;

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

    public AutomationOptions Automation { get; set; } = new();
}

/// <summary>
/// Rules that act as the account by themselves. The rules are read from
/// <c>Control:Automation:Rules</c> by <c>AutomationConfigRules</c>, because their actions don't
/// bind to fixed types.
/// </summary>
public sealed class AutomationOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Roles that may create, change and run rules from Discord. Members of
    /// <see cref="ControlOptions.AuthorizedRoleIds"/> may only look at rules and approve runs.
    /// </summary>
    public List<ulong> ManagerRoleIds { get; set; } = [];

    /// <summary>Where reports and approvals go unless a rule says otherwise. Defaults to the status channel.</summary>
    public ulong? ReportChannelId { get; set; }

    /// <summary>Caps actions as the account across all rules. Further actions wait for a slot.</summary>
    public int MaxActionsPerMinute { get; set; } = 10;

    public int MaxActionsPerHour { get; set; } = 120;

    /// <summary>Caps automated DMs to one player, so two bots can't talk to each other forever.</summary>
    public int MaxDmsPerPlayerPerHour { get; set; } = 5;

    /// <summary>How many times a rule may raise an event that starts another rule, in one chain.</summary>
    public int MaxChainDepth { get; set; } = 3;

    /// <summary>The most players one Raise with ForEach may reach.</summary>
    public int MaxFanOut { get; set; } = 200;

    /// <summary>How long an action that needs the socket waits for it before the run fails.</summary>
    public TimeSpan OfflineGrace { get; set; } = TimeSpan.FromMinutes(10);
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
    /// <summary>Whether the bot watches for the event at all. Rules only start on watched events.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the event posts its card in Discord. Off keeps the event for rules, for when an
    /// automation announces it instead. Direct messages have no card, only their forum post.
    /// </summary>
    public bool Card { get; set; } = true;

    /// <summary>Overrides <see cref="DiscordOptions.DefaultChannelId"/> for this event.</summary>
    public ulong? ChannelId { get; set; }

    public List<ulong> PingRoleIds { get; set; } = [];
}

public sealed class DatabaseOptions
{
    public string Path { get; set; } = "/data/shadow.db";
}

/// <summary>
/// The API that lets other instances use this one as a source. Each client has its own token, and
/// sees only the events and data it is allowed.
/// </summary>
public sealed class ExternalApiOptions
{
    public bool Enabled { get; set; }

    public int Port { get; set; } = 8080;

    /// <summary>How long served events are kept, so a client that was away can catch up.</summary>
    public TimeSpan EventRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The instances allowed to connect, by a name used in logs.</summary>
    public Dictionary<string, ExternalApiClientOptions> Clients { get; set; } = [];
}

/// <summary>What one client of the API may see. Nothing is shared by default.</summary>
public sealed class ExternalApiClientOptions
{
    /// <summary>The token the client sends. Best kept in a Docker secret.</summary>
    public string Token { get; set; } = "";

    /// <summary>The triggers whose events it receives, such as <c>ClubApplicationReceived</c> or <c>Custom:vip</c>.</summary>
    public List<string> Events { get; set; } = [];

    /// <summary>The data it may read.</summary>
    public List<ExternalApiData> Data { get; set; } = [];
}

/// <summary>The kinds of data a source serves.</summary>
public enum ExternalApiData
{
    /// <summary>How one player relates to the account: friend, applicant, member.</summary>
    Players,

    /// <summary>The account's id and club.</summary>
    Account,

    /// <summary>The account's friends, friend requests, club members and applications.</summary>
    Lists
}

/// <summary>Another system this instance reads from.</summary>
public sealed class SourceOptions
{
    public const string ShadowBotType = "ShadowBot";

    /// <summary>What kind of system it is. Only another shadow bot exists so far.</summary>
    public string Type { get; set; } = ShadowBotType;

    /// <summary>Where it is, such as <c>https://dragon.example.com/</c>, or plain http on a private network.</summary>
    public Uri? Url { get; set; }

    /// <summary>The token the source gave this instance. Best kept in a Docker secret.</summary>
    public string Token { get; set; } = "";

    /// <summary>How long the source may be unreachable before the status channel hears about it.</summary>
    public TimeSpan AlertAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the source may be unreachable before the health check fails. Null never fails it.</summary>
    public TimeSpan? UnhealthyAfter { get; set; } = TimeSpan.FromMinutes(30);
}
