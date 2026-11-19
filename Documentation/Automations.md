# Automations

Automations are rules that act as the account by themselves. A rule says what starts it, when it applies, and what it does, such as sending a welcome DM to everyone who accepts a friend request.

## Contents

- [Getting started](#getting-started)
- [Writing rules](#writing-rules)
- [Triggers](#triggers)
- [Conditions and templates](#conditions-and-templates)
- [Steps](#steps)
- [Limits and safety](#limits-and-safety)
- [Reports and approvals](#reports-and-approvals)
- [Discord commands](#discord-commands)
- [Settings](#settings)
- [Examples](#examples)
- [How it works](#how-it-works)

Rules can also read other instances of the bot, and start on their events. See [Headless mode and external sources](ExternalSources.md).

## Getting started

1. Set `Control:Automation:Enabled` to `true`.
2. Set `Control:Automation:ManagerRoleIds` to the roles that may write rules.
3. Restart, then run `/automation create` in Discord and paste a rule:

```json
{
  "Name": "welcome-new-friends",
  "On": "FriendRequestAccepted",
  "DryRun": true,
  "Do": [{ "SendDm": "Hi {{ player.nick }}, thanks for accepting!" }]
}
```

With `DryRun`, the rule only reports what it would do. Try it with `/automation test welcome-new-friends player:<profile link>`, then remove `DryRun` with `/automation edit`.

A rule can only run on an event that is switched on in `Events`. The rule above needs `Events:FriendRequestAccepted:Enabled`, which is on by default. To have a rule post instead of the bot's own card, keep the event on and set its `Card` to `false`, such as `Events:ClubApplicationReceived:Card`.

## Writing rules

Rules are JSON. They can be created in Discord, which stores them in the database, or written in the configuration under `Control:Automation:Rules`, where they can be switched off from Discord but not changed.

| Property | Default | Meaning |
|---|---|---|
| `Name` | Required | Lowercase letters, digits and dashes |
| `Description` | None | Shown in `/automation list` |
| `On` | Required | What starts the rule, see [Triggers](#triggers) |
| `Cron`, `TimeZone` | `UTC` | The schedule, when `On` is `Schedule` |
| `When` | Always | A condition, see [Conditions and templates](#conditions-and-templates) |
| `Do` | Required | The steps, see [Steps](#steps) |
| `Enabled` | `true` | Whether the rule runs |
| `DryRun` | `false` | Only describe the actions, and report them |
| `MaxAge` | `15m` | Skip events older than this, such as messages found after downtime |
| `OncePerPlayer` | `false` | `true` for once ever, or a duration such as `"30d"` for once per period |
| `PlayerCooldown` | None | The least time between two runs for the same player |
| `Cooldown` | None | The least time between two runs of the rule |
| `MaxRunsPerHour` | None | The most runs that may start in an hour |
| `Priority` | `0` | Higher runs first when several rules match one event |
| `StopOtherRules` | `false` | When this rule runs, other rules skip the same event |
| `Report` | `Failures` | `None`, `Failures` or `All` |
| `ReportChannelId` | See [Settings](#settings) | Where reports, approvals and posts go |

Durations are written as `30s`, `10m`, `2h`, `1d`, or `hh:mm:ss`. Property names aren't case-sensitive, and comments and trailing commas are allowed. Every problem is listed with its path when a rule is saved, such as `Do[1].Then[0].SendDm: needs text.`

## Triggers

`On` takes one of these:

| Trigger | Starts on | `event` fields |
|---|---|---|
| `DmMessage` | A DM from someone else | `text`, `payload_type`, `sent_at` |
| `ClubChat` | A club chat message from someone else | `text`, `payload_type`, `sent_at`, `club_id` |
| `FriendRequestReceived` | A friend request | |
| `FriendRequestAccepted` | Someone accepting the account's request | |
| `FriendPresence` | A friend coming online or going offline | `online`, `activity` |
| `ClubApplicationReceived` | An application to the club | `nick` |
| `ClubMemberJoined`, `ClubMemberLeft`, `ClubRoleChanged` | Roster changes | `nick`, `role` |
| `ClubMissionBoard` | Mission board progress | `board_cleared` |
| `RankedDivisionChanged` | A new division | `before`, `after`, `before_number`, `after_number`, `rating` |
| `SeasonChanged` | A new season | `season_id` |
| `InviteGame`, `InviteChallenge`, `InviteTeam`, `InviteParty`, `InviteClub`, `ClubApplicationAccepted`, `BadgeUnlocked`, `StreakAtRisk`, `StreakSaved`, `ModerationAction`, `RatingRefund`, `ActivityReaction`, `AccountOther` | Notifications | `notification_id`, `type`, `sent_by_nick`, `sent_at` |
| `Schedule` | A cron schedule, such as `"0 18 * * *"` for 18:00 daily | `scheduled_at` |
| `Manual` | Only `/automation run` | `requested_by`, `text` |
| `Custom:<name>` | A `Raise` step in another rule | Whatever the raising rule sends, plus `source_rule`, `source_run` |
| `Source:<source>:<trigger>` | An event of an [external source](ExternalSources.md), such as `Source:dragon:ClubApplicationReceived` | The event's fields at the source, plus `source` |

Every event also has `event.trigger` and `event.occurred_at`. Any rule can be started by hand with `/automation run`.

Messages the account sent itself never start a rule, whether they came from the bot or another login, so a rule can't answer itself.

`ClubApplicationReceived` needs `GeoGuessr:Club:Enabled`, and the account must be allowed to manage applications. GeoGuessr sends nothing when someone applies, so applications are read every `Polling:ClubApplicationsInterval`, two minutes by default.

## Conditions and templates

Conditions and texts use [Scriban](https://github.com/scriban/scriban/blob/master/doc/language.md). A condition is an expression, and a text puts expressions in `{{ }}`:

```json
"When": "player.level >= 20 && player.country == 'DE'",
"Do": [{ "SendDm": "Hi {{ player.nick }}, you're level {{ player.level }}!" }]
```

Scriban's functions are available, such as `string.contains`, `string.downcase`, `regex.match`, `date.to_string` and `math.round`. A pipe passes a value on: `event.text | string.downcase | string.contains 'discord'`. Text can branch too: `{{ if player.is_pro }}Thanks for your support!{{ end }}`.

**`player`** is the player the event is about. It's missing for events without one, such as schedules.

| Field | Meaning |
|---|---|
| `id`, `nick`, `profile_url` | Who it is |
| `is_friend`, `has_requested`, `was_requested` | A friend, sent the account a request, got one from it |
| `is_club_member`, `has_applied` | In the club, has a pending application |
| `found` | The profile could be read |
| `country`, `level`, `is_pro`, `account_type` | From the profile |
| `created_at`, `account_age_days` | When the account was made |
| `banned`, `chat_banned`, `suspended_until` | Standing |
| `club.id`, `club.tag`, `club.name`, `club.level` | Their club, or null |
| `ranked.division`, `ranked.rating`, `ranked.peak_rating`, `ranked.recent_wins`, `ranked.recent_games` | Ranked, or null |

Reading through a field that is null gives null rather than an error, so `player.club.id != account.club_id` is true for someone without a club.

The fields in the first three rows are known without asking GeoGuessr. A rule that reads any other field costs a profile lookup per run.

Also available are `rule.name`, `run.id`, `run.trigger`, `run.depth`, `run.dry_run`, `run.manual`, `account.id`, `account.club_id`, and `now`, the current time in UTC.

**`sources`** holds what [external sources](ExternalSources.md#reading-a-source) know, such as `sources.dragon.player.has_applied` for the run's player at the source named `dragon`, or `sources.dragon.account.club_id`. A rule reads a source when it starts, once for each part its scripts name.

Scripts only see these values. They can't read files or reach anything else, and they stop after 2 seconds, 1000 loop iterations, or a slow regular expression.

## Steps

`Do` is a list of steps, which run in order. Each step is an object with one key. Most steps also have a short form.

**As the account:**

| Step | Short form | Long form |
|---|---|---|
| `SendDm` | `"text"` to the player | `{ "Text": "...", "To": "{{ event.x }}" }` |
| `SendClubMessage` | `"text"` | `{ "Text": "..." }` |
| `SendFriendRequest` | `true` for the player | `{ "Player": "<id or template>" }` |
| `AcceptFriendRequest`, `DeclineFriendRequest` | `true` | `{ "Player": "..." }` |
| `AcceptClubApplication`, `DeclineClubApplication` | `true` | `{ "Player": "..." }` |

A friend step reads the friend list first and does nothing when there's nothing to do, such as accepting a request that was already answered, or sending one to someone who is already a friend. `DeclineFriendRequest` stops that person from sending another request, as it does from the site.

GeoGuessr cuts messages at 300 characters, so longer ones fail. DMs need the socket.

**In Discord:**

| Step | Short form | Long form |
|---|---|---|
| `PostDiscord` | `"text"` | `{ "Text": "...", "Title": "...", "ChannelId": 1, "PingRoleIds": [2], "Style": "Success" }` |
| `ForumNote` | `"text"` in the player's post | `{ "Text": "...", "Player": "..." }` |

Discord text may use markdown. Values put into it, such as names and messages, are escaped, so they can't format, link or ping. A post pings only the roles in its `PingRoleIds`.

**Flow:**

| Step | Example | Meaning |
|---|---|---|
| `If` | `{ "If": "player.level > 50", "Then": [...], "Else": [...] }` | Branch. `Else` is optional |
| `Wait` | `{ "Wait": "30s" }` | Pause, up to 7 days. Waits survive restarts |
| `RequireApproval` | `{ "RequireApproval": "Accept {{ player.nick }}?" }` | Pause until someone approves in Discord, see [Reports and approvals](#reports-and-approvals). The long form takes `Prompt`, `Timeout` (default `1d`) and `ChannelId` |
| `Raise` | `{ "Raise": "vip" }` | Start the rules with `On: "Custom:vip"`, for the same player |
| `Raise` | `{ "Raise": { "Event": "vip", "ForEach": "Friends", "Data": { "why": "{{ rule.name }}" } } }` | Start them once per friend. `ForEach` also takes `FriendRequests`, `ClubMembers` and `ClubApplications`, or a source's group such as `Source:dragon:ClubApplications`, and `Player` picks one player instead |
| `Stop` | `{ "Stop": "Nothing to do" }` | End the run |

A rule has at most 50 steps, counting the jumps `If` adds.

## Limits and safety

- **Late events.** Events older than `MaxAge` are skipped, so catch-up after downtime doesn't answer a day-old message.
- **First sync.** Events recorded silently on the first start never start a rule.
- **Once per player.** `OncePerPlayer` is remembered forever, even after runs are cleaned up.
- **Rate.** All rules together take at most `MaxActionsPerMinute` and `MaxActionsPerHour` actions as the account. Actions over the limit wait for a slot.
- **DMs per player.** One player gets at most `MaxDmsPerPlayerPerHour` automated DMs. Further ones are dropped rather than delayed, so two bots answering each other can't queue messages forever.
- **Chains.** A `Raise` can only start a chain `MaxChainDepth` deep, and reach `MaxFanOut` players.
- **At most once.** An action as the account is never repeated. If the bot stops in the middle of one, the run fails and says so, because the action may already have happened.
- **Offline.** A DM waits up to `OfflineGrace` for the socket, then the run fails.
- **Edits.** A run uses the rule as it was when it started. Switching a rule off or deleting it cancels its unfinished runs.
- **Manual runs.** `/automation run` ignores `MaxAge` and the rule's limits, but not its condition or the global ones.
- **Audit.** Every action is recorded in the audit log as `auto:<rule>:<action>`, and every change to a rule under the person who made it.

## Reports and approvals

A report is a card with the outcome and every step the run took. Rules report according to `Report`: `Failures` reports failed and expired runs, `All` adds completed and rejected ones, and `None` reports nothing. Dry runs and manual runs always report. Skipped runs only report when they were dry or manual.

A `RequireApproval` step posts a card with the prompt, what happens on approval, and Approve and Reject buttons. Members of `Control:AuthorizedRoleIds` or `Control:Automation:ManagerRoleIds` may answer. Without an answer, the run expires after its timeout.

Reports, approvals and posts go to the rule's `ReportChannelId`, else `Control:Automation:ReportChannelId`, else the status channel.

## Discord commands

| Command | Who | What |
|---|---|---|
| `/automation list` | Authorized and managers | Every rule, whether it's on, and problems |
| `/automation show <name>` | Authorized and managers | The rule and its recent runs |
| `/automation test <name> [player] [text]` | Authorized and managers | A dry run that records nothing |
| `/automation runs [name]` | Authorized and managers | Recent runs |
| `/automation sources` | Authorized and managers | Whether each [external source](ExternalSources.md) is connected, and its last event |
| `/automation create` | Managers | Opens a form for the rule |
| `/automation edit <name>` | Managers | Opens the rule in a form |
| `/automation enable`, `disable <name>` | Managers | Switch a rule on or off |
| `/automation delete <name>` | Managers | Delete a rule made in Discord |
| `/automation run <name> [player] [text]` | Managers | Run a rule now |
| `/automation cancel <run>` | Managers | Cancel an unfinished run |

`player` takes a user id or a profile link. `text` fills `event.text`. Answers are only shown to whoever asked. A form holds 4000 characters, so longer rules belong in the configuration, or can be split into rules chained with `Raise`.

## Settings

Under `Control:Automation`:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Switches automations and their commands on |
| `ManagerRoleIds` | Empty | Who may change and run rules. Empty means nobody |
| `ReportChannelId` | Status channel | Where reports and approvals go |
| `MaxActionsPerMinute`, `MaxActionsPerHour` | `10`, `120` | Actions as the account, across all rules |
| `MaxDmsPerPlayerPerHour` | `5` | Automated DMs to one player |
| `MaxChainDepth` | `3` | How deep `Raise` may chain |
| `MaxFanOut` | `200` | How many players one `ForEach` may reach |
| `OfflineGrace` | `00:10:00` | How long a DM waits for the socket |
| `Rules` | Empty | Rules in the configuration |

Configured rules are checked at startup, with the rest of the configuration. To keep them out of environment variables in Docker, put them in a file and mount it as `/app/appsettings.Local.json`:

```yaml
    volumes:
      - shadow-data:/data
      - ./automations.json:/app/appsettings.Local.json:ro
```

```json
{ "Control": { "Automation": { "Rules": [ { "Name": "...", "On": "...", "Do": [] } ] } } }
```

## Examples

**Welcome new friends**, once each, after a short pause:

```json
{
  "Name": "welcome-new-friends",
  "On": "FriendRequestAccepted",
  "OncePerPlayer": true,
  "Do": [
    { "Wait": "1m" },
    { "SendDm": "Hi {{ player.nick }}! Thanks for adding me. Say hi any time." }
  ]
}
```

**Befriend club applicants**, and let experienced ones in after approval:

```json
{
  "Name": "club-applicants",
  "On": "ClubApplicationReceived",
  "Do": [
    { "SendFriendRequest": true },
    { "If": "player.level >= 50 && !player.banned", "Then": [
      { "RequireApproval": "{{ player.nick }} is level {{ player.level }}. Accept them?" },
      { "AcceptClubApplication": true },
      { "SendDm": "Welcome to the club, {{ player.nick }}!" }
    ] }
  ]
}
```

**Answer DMs while away**, once an hour per person, and not to old messages:

```json
{
  "Name": "away",
  "On": "DmMessage",
  "MaxAge": "5m",
  "PlayerCooldown": "1h",
  "Do": [{ "SendDm": "I'm away right now and will answer later." }]
}
```

**Answer questions about Discord**, before anything else matches:

```json
{
  "Name": "discord-link",
  "On": "DmMessage",
  "Priority": 10,
  "StopOtherRules": true,
  "When": "event.text | string.downcase | string.contains 'discord'",
  "Do": [{ "SendDm": "Our Discord is at discord.gg/example" }]
}
```

**Accept friend requests from established players**, and leave the rest for people:

```json
{
  "Name": "auto-accept",
  "On": "FriendRequestReceived",
  "When": "player.level >= 30 && player.account_age_days >= 90 && !player.banned",
  "Report": "All",
  "Do": [{ "AcceptFriendRequest": true }]
}
```

**Remind the club every evening**, in the club's time zone:

```json
{
  "Name": "daily-reminder",
  "On": "Schedule",
  "Cron": "0 19 * * *",
  "TimeZone": "Europe/Berlin",
  "Do": [{ "SendClubMessage": "Daily challenge closes soon. Good luck!" }]
}
```

**Weekly hello to every friend**, with a second rule doing the per-friend work:

```json
{ "Name": "weekly-hello", "On": "Schedule", "Cron": "0 12 * * 0",
  "Do": [{ "Raise": { "Event": "hello", "ForEach": "Friends" } }] }
```

```json
{ "Name": "hello", "On": "Custom:hello", "OncePerPlayer": "6d",
  "Do": [{ "SendDm": "Happy Sunday, {{ player.nick }}! Up for a game?" }] }
```

**Celebrate a new division** in Discord:

```json
{
  "Name": "promotion",
  "On": "RankedDivisionChanged",
  "When": "event.after_number > event.before_number",
  "Do": [{ "PostDiscord": { "Title": "Promoted!", "Text": "Now in **{{ event.after }}**", "Style": "Success" } }]
}
```

## How it works

When an event commits, the rules for its trigger each get a run in the same transaction, so an event starts its rules exactly once. Conditions aren't checked then, because they may need GeoGuessr, and a slow call would stall every other event.

A worker then takes runs oldest first. It checks the run's age, condition and limits, and then steps through the rule, saving after every step. A `Wait` or an approval saves the run with the time to look at it again, so nothing waits in memory. Runs end with their event data and step log removed, so message text isn't kept.

A DM sent by a rule comes back on the socket like any other. The bot recognises it and shows it in the partner's post as sent by the rule.
