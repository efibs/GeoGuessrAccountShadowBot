# Headless mode and external sources

One instance of the bot shadows one account. When a rule needs to know what happens on another account, such as applications to a second club whose admin is a different account, run a second instance for that account and let the first read it as an **external source**.

- **Headless mode** runs an instance without Discord. It needs no bot token.
- **The external API** lets an instance serve its events and data to the instances you allow.
- **Sources** are what an instance reads from. Rules start on a source's events and read its data.

## Contents

- [Example: two clubs](#example-two-clubs)
- [Headless mode](#headless-mode)
- [Serving other instances](#serving-other-instances)
- [Reading a source](#reading-a-source)
- [Security](#security)
- [When one side is down](#when-one-side-is-down)
- [Settings](#settings)
- [The API](#the-api)
- [How it works](#how-it-works)

## Example: two clubs

The main instance shadows the admin of *Dragon's Den* and runs in Discord. A second, headless instance shadows the admin of *Dragon*, whose applications the first account can't see. The main instance befriends everyone who applies to either club, and greets them according to where they applied.

```yaml
services:
  shadow-bot:
    image: ghcr.io/efibs/geoguessr-account-shadow-bot:latest
    # ...as in compose.example.yaml, plus:
    secrets:
      - GeoGuessr__NcfaToken
      - Discord__Token
      - Sources__dragon__Token
    volumes:
      - shadow-data:/data
      - ./automations.json:/app/appsettings.Local.json:ro

  dragon-headless:
    image: ghcr.io/efibs/geoguessr-account-shadow-bot:latest
    restart: unless-stopped
    read_only: true
    tmpfs:
      - /tmp
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    secrets:
      - source: dragon-ncfa
        target: GeoGuessr__NcfaToken
      - source: Sources__dragon__Token
        target: ExternalApi__Clients__denkeeper__Token
    environment:
      Discord__Enabled: "false"
      Events__DmMessage__Enabled: "false"
      GeoGuessr__Club__Enabled: "true"
      Events__ClubApplicationReceived__Enabled: "true"
      ExternalApi__Enabled: "true"
      ExternalApi__Clients__denkeeper__Events__0: "ClubApplicationReceived"
      ExternalApi__Clients__denkeeper__Data__0: "Players"
    volumes:
      - dragon-data:/data
    # No ports: the main instance reaches it on the compose network.

secrets:
  dragon-ncfa:
    file: ./secrets/dragon-ncfa-token.txt
  # One token, known to both sides. Generate it with: openssl rand -base64 32
  Sources__dragon__Token:
    file: ./secrets/dragon-source-token.txt

volumes:
  shadow-data:
  dragon-data:
```

The main instance's `automations.json` names the source and holds the rules:

```json
{
  "Sources": {
    "dragon": { "Url": "http://dragon-headless:8080" }
  },
  "Control": {
    "Automation": {
      "Rules": [
        {
          "Name": "befriend-dragon-applicants",
          "Description": "Sends a friend request to everyone who applies to Dragon",
          "On": "Source:dragon:ClubApplicationReceived",
          "OncePerPlayer": true,
          "MaxAge": "2d",
          "Do": [{ "SendFriendRequest": true }]
        },
        {
          "Name": "friend-request-accepted",
          "On": "FriendRequestAccepted",
          "OncePerPlayer": true,
          "MaxAge": "2d",
          "Do": [
            { "Wait": "5s" },
            { "If": "player.has_applied",
              "Then": [{ "SendDm": "Hi :wave: I saw your application to Dragon's Den. ..." }],
              "Else": [
                { "If": "sources.dragon.player.has_applied",
                  "Then": [{ "SendDm": "Hi :wave: I saw your application to Dragon. ..." }],
                  "Else": [
                    { "If": "player.club.id != account.club_id",
                      "Then": [{ "SendDm": "Hi :wave: This is the admin account of Dragon's Den ..." }] }
                  ] }
              ] }
          ]
        }
      ]
    }
  }
}
```

The headless instance only polls Dragon's applications, so its account needs the permission to manage them, as for [`ClubApplicationReceived`](Automations.md#triggers) on any instance.

## Headless mode

Set `Discord:Enabled` to `false`. The instance then watches the account as usual, runs the rules in its configuration, and serves other instances, but starts no Discord bot. `Discord:Token`, `Discord:GuildId`, `Discord:DefaultChannelId` and `Forum:ChannelId` aren't needed.

Nothing is queued for Discord. Status messages, such as a rejected token, and automation reports go to the log instead, and cards and chat lines are dropped. What only works through Discord is refused at startup: `Control:DmReply`, `Control:FriendRequestButtons`, `Control:ClubChat`, `Forum:ChannelId`, and rules with `PostDiscord`, `ForumNote`, `RequireApproval` or `On: Manual`.

`Events:DmMessage` is on by default. Switch it off on a headless instance unless a rule or a client uses it.

## Serving other instances

Set `ExternalApi:Enabled` to `true`, and list each instance that may connect under `ExternalApi:Clients`, by a name of your choosing:

```json
"ExternalApi": {
  "Enabled": true,
  "Port": 8080,
  "Clients": {
    "denkeeper": {
      "Token": "<from a secret>",
      "Events": ["ClubApplicationReceived", "Custom:vip"],
      "Data": ["Players", "Account", "Lists"]
    }
  }
}
```

A client gets nothing that isn't listed.

| Setting | What it allows |
|---|---|
| `Events` | Receiving these events. Any trigger from [the list](Automations.md#triggers) that's switched on in `Events`, or `Custom:<name>` for what a rule raises |
| `Data: Players` | Reading how one player relates to this account: `is_friend`, `has_requested`, `was_requested`, `is_club_member`, `has_applied` |
| `Data: Account` | Reading this account's `id` and `club_id` |
| `Data: Lists` | Reading the ids of this account's friends, friend requests, club members and club applications |

Events are recorded only if some client may receive them, and only events that would start a rule here: nothing from a first sync, and no message the account sent itself. `DmMessage` and `ClubChat` carry the message text, so allow them only to instances you trust with it. They are kept for `EventRetention`, seven days by default.

Serving never costs a GeoGuessr call. Everything is answered from what this instance already knows.

Both instances can serve each other. A rule on one can then start rules on the other, by raising a custom event the other is allowed to receive.

## Reading a source

Name each source under `Sources`, with a name of lowercase letters, digits and underscores, since rules write it as `sources.<name>`:

```json
"Sources": {
  "dragon": { "Type": "ShadowBot", "Url": "http://dragon-headless:8080", "Token": "<from a secret>" }
}
```

Sources are followed while `Control:Automation:Enabled` is on.

**Events** start rules with `On: "Source:<name>:<trigger>"`, such as `Source:dragon:ClubApplicationReceived` or `Source:dragon:Custom:vip`. The rule sees the event's fields as it does on the source, plus `event.source`. `player` is the player the event is about, as seen from *this* account, so `SendFriendRequest` and `SendDm` act on them as usual. `MaxAge` counts from when the event happened at the source.

**Data** is read with `sources.<name>.player.<field>`, for the run's player, and `sources.<name>.account.<field>`:

```json
"When": "sources.dragon.player.has_applied && !player.is_friend"
```

A rule reads a source only if one of its scripts names it, once per section it names, when the run starts. Write the name out as `sources.dragon.player`, because a name built at runtime, such as `sources[name]`, isn't fetched and reads as null.

**Lists** feed a `Raise`: `"ForEach": "Source:dragon:ClubApplications"` starts the custom event once for each player who applied to the source's club. `Friends`, `FriendRequests` and `ClubMembers` work the same way. `MaxFanOut` applies.

`/automation sources` shows each source, whether it's connected, and when its last event came.

## Security

- **Tokens.** Every request carries a client's token, as a bearer token. A token must be at least 32 characters, and two clients can't share one. Generate one with `openssl rand -base64 32`, and keep it in a Docker secret, such as `ExternalApi__Clients__<client>__Token` on the source and `Sources__<name>__Token` on the reader. Tokens are compared in constant time, and redacted from logs.
- **Allow-lists.** A client only receives the events and reads the data it was given. Anything else is answered with 403, and a wrong token with 401.
- **Transport.** A source's `Url` must use https, unless it stays on a private network: loopback, private and link-local addresses, `100.64.0.0/10` as used by overlay networks such as Tailscale, or a host name without a dot, such as a compose service. For anything else, put the API behind a reverse proxy with TLS. The client never follows redirects, so the token can't be sent anywhere but the configured address.
- **Exposure.** Don't publish the port unless another host needs it. Between containers on one host, the compose network is enough.
- **No actions.** The API only reads. No client can make the account do anything.

## When one side is down

- **The source is down.** The reader reconnects with a backoff of up to a minute. After `AlertAfter`, five minutes by default, the status channel hears about it, and again when it's back. A source that refuses the token is reported at once, and tried again every five minutes. The health check fails once a source has been down for `UnhealthyAfter`, 30 minutes by default.
- **A rule reads a source that's down.** The run waits 30 seconds and tries again, as it does when GeoGuessr is down, and fails after 20 tries.
- **Missed events.** The reader saves its position in the source's feed with the runs each event starts, so after an outage on either side it picks up where it stopped, and starts each rule once per event. Old events are skipped by `MaxAge` as usual. If the reader was away longer than the source's `EventRetention`, it says that events may be missing.
- **First connection.** A reader that never connected before starts at the newest event, so events from before it was set up start nothing.
- **A source starts over.** If its database was replaced, the reader notices that the feed is behind what it saw, says so, and follows from the newest event.

## Settings

Under `Discord`:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` runs the instance headless |

Under `ExternalApi`:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Serves other instances |
| `Port` | `8080` | The port it listens on, on every interface |
| `EventRetention` | `7.00:00:00` | How long events are kept for clients that were away, between one hour and 90 days |
| `Clients:<name>:Token` | Required | The client's token |
| `Clients:<name>:Events` | Empty | The triggers it receives |
| `Clients:<name>:Data` | Empty | `Players`, `Account` and `Lists` |

Under `Sources:<name>`:

| Setting | Default | Meaning |
|---|---|---|
| `Type` | `ShadowBot` | What kind of source it is. Another instance of this bot is the only kind so far |
| `Url` | Required | Where it is |
| `Token` | Required | The token the source lists for this instance |
| `AlertAfter` | `00:05:00` | How long it may be down before the status channel hears about it |
| `UnhealthyAfter` | `00:30:00` | How long it may be down before the health check fails. Empty never fails it |

## The API

Every endpoint needs `Authorization: Bearer <token>`, and answers JSON with snake_case names.

| Endpoint | Needs | Answer |
|---|---|---|
| `GET /api/v1/account` | `Account` | `{ "id": "...", "club_id": "..." }` |
| `GET /api/v1/players/{id}` | `Players` | `{ "id": "...", "is_friend": false, "has_requested": false, "was_requested": true, "is_club_member": false, "has_applied": true }` |
| `GET /api/v1/lists/{list}` | `Lists` | `{ "ids": ["..."] }`, for `friends`, `friendrequests`, `clubmembers` or `clubapplications` |
| `GET /api/v1/events` | Some `Events` | A server-sent event stream |

The stream starts with a `hello` with the oldest and newest id in the feed, `{ "oldest": 1, "newest": 42 }`. Then come `event`s, each with its id as the SSE id:

```json
{ "id": 43, "trigger": "ClubApplicationReceived", "player_id": "...", "occurred_at": "...", "recorded_at": "...", "data": { "nick": "..." } }
```

A `ping` follows every 15 seconds without an event, and a reader that hears nothing for 45 seconds reconnects. Send `Last-Event-ID` to get the events after that id, and leave it out to start from the newest. A `Last-Event-ID` ahead of the feed is treated like none.

It answers 503 while the bot is still starting.

## How it works

```
source:  event ─> EventProcessor ─> feed row (same transaction) ─> ExternalApiServer ─> SSE
reader:  SSE ─> SourceEventWorker ─> runs + position (same transaction) ─> AutomationWorker
         AutomationEngine ─> IExternalSource ─> GET /api/v1/players/{id}
```

On the source, an event that some client may receive is written to the feed in the transaction that processes it, like the runs it starts locally. Raised custom events are written when the run that raised them saves. The feed's ids only grow, and pruning always keeps the newest row, so a position stays meaningful.

On the reader, `SourceEventWorker` holds one stream per source. Each event creates the runs of the rules listening to it, and saves the position, in one transaction. A crash before the commit means the event comes again, and after it means it doesn't. The engine fetches what a rule's scripts read from sources before the run starts, since scripts can't wait for a request.

Sources sit behind `IExternalSource`, which has sections of data, groups of players, and events. Another kind of source, such as a weather service, would be another implementation and a `Type`, with the same rule syntax.
