# GeoGuessrAccountShadowBot

[![CI](https://github.com/efibs/GeoGuessrAccountShadowBot/actions/workflows/ci.yml/badge.svg)](https://github.com/efibs/GeoGuessrAccountShadowBot/actions/workflows/ci.yml)
[![CodeQL](https://github.com/efibs/GeoGuessrAccountShadowBot/actions/workflows/codeql.yml/badge.svg)](https://github.com/efibs/GeoGuessrAccountShadowBot/actions/workflows/codeql.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License: GPL v2 or later](https://img.shields.io/badge/License-GPL%20v2%2B-blue.svg)](LICENSE)

A Discord bot that shadows one GeoGuessr account. It mirrors what happens on the account into your Discord server, and lets the people you choose answer from Discord.

## Contents

- [Disclaimer](#disclaimer)
- [Features](#features)
- [Quick start](#quick-start)
- [Discord setup](#discord-setup)
- [Configuration](#configuration)
- [How it behaves](#how-it-behaves)
- [Data it stores](#data-it-stores)
- [Development](#development)
- [Releases](#releases)
- [License](#license)

## Disclaimer

This is an unofficial project, not affiliated with GeoGuessr. Please read this before running it.

- **It uses an undocumented API.** GeoGuessr can change it at any time, and the bot may stop working when it does.
- **It logs in with a session cookie.** Automating an account this way may breach GeoGuessr's terms of service. Use an alt account, at your own risk.
- **Treat the `_ncfa` cookie like a password.** Anyone who has it can act as the account.
- **It copies private messages.** People who message the account don't know their messages end up in Discord. Keep the forum channel restricted to people who should see them, and consider telling those you talk to.

## Features

Push-based wherever GeoGuessr allows it, so events arrive within moments. Every feature has its own switch.

| Area | What you get |
|---|---|
| Direct messages | One Discord forum post per chat partner, with the whole conversation and a link to their profile |
| Friends | New requests with a profile card (picture, link, club, level, rating, standing), your requests being accepted, optional presence |
| Invites | Game, challenge, team, party and club invites, club applications accepted |
| Account | Badges, daily streak at risk or saved, moderation actions, rating refunds, reactions |
| Club | Club chat, members joining or leaving, role changes, mission board |
| Ranked | Division and season changes, by slow configurable polling |

From Discord, members of the roles you configure can:

- **Reply to DMs** by typing in the partner's forum post. Start a message with `//` to keep it in Discord.
- **Accept or decline friend requests** with buttons. Declining asks for confirmation, because GeoGuessr then stops that person from sending another request, even if you become friends later.
- **Post in club chat** with `/shadow club-say`.

## Quick start

1. Get the `_ncfa` cookie of the account from your browser's developer tools.
2. Create a Discord application and bot, as described in [Discord setup](#discord-setup).
3. Copy [`compose.example.yaml`](compose.example.yaml) to `compose.yaml`, and fill in your ids.
4. Put the two tokens in `secrets/ncfa-token.txt` and `secrets/discord-token.txt`.
5. Run `docker compose up -d`.

On the first start, the bot records what already exists without announcing it, and posts a short summary instead of flooding your server.

## Discord setup

1. Create an application in the [developer portal](https://discord.com/developers/applications), add a bot, and copy its token.
2. Create a **forum channel** for conversations and a normal text channel for everything else.
3. Invite the bot with these permissions on those channels: View Channel, Send Messages, Send Messages in Threads, Create Posts, Manage Threads, Add Reactions, Embed Links, Read Message History.
4. To reply from Discord, also enable the **Message Content** intent in the portal and set `Discord:MessageContentIntent` to `true`.

The forum must not require tags, because the bot creates posts without any.

## Configuration

Everything has a default in [`appsettings.json`](GeoGuessrAccountShadowBot.Host/appsettings.json), which also serves as the full reference. Override a value with an environment variable, using `__` for nesting, such as `Events__BadgeUnlocked__Enabled=false`. Docker secrets named the same way work too.

| Section | Purpose |
|---|---|
| `GeoGuessr` | Token, socket settings, yield times, club |
| `Polling` | Catch-up and ranked intervals, paging limits |
| `Discord` | Bot token, server, default and status channels |
| `Forum` | The forum channel for conversations |
| `Control` | Authorized roles, and one switch per action |
| `Events` | One entry per event: `Enabled`, optional `ChannelId` and `PingRoleIds` |
| `Database` | Where the SQLite file lives |

The required values are `GeoGuessr:NcfaToken`, `Discord:Token`, `Discord:GuildId`, `Discord:DefaultChannelId` and `Forum:ChannelId`. Actions are off until you enable them and set `Control:AuthorizedRoleIds`. The bot checks the configuration at startup and lists every problem at once.

Set `GeoGuessr:Socket:Enabled` to `false` for polling only. Mirroring then lags by about two minutes, and replies aren't possible.

## How it behaves

- **Catch-up.** After any downtime, the bot compares GeoGuessr's state with what it has processed and delivers what it missed, oldest first. Late messages show when they were really sent.
- **Other logins.** A browser logged in on the same account works alongside the bot. Messages sent from there are mirrored and labelled.
- **Superseded connections.** If GeoGuessr ever replaces the bot's socket, the bot stays off it for 15 minutes, doubling up to an hour, while catching up over HTTP. Replies are refused during that time, with a reason.
- **Expired token.** The bot pings the status channel and waits. Replace the token and restart it.
- **No trace on the account.** Reading never marks anything as read.
- **Duplicates.** A crash at exactly the wrong moment can repeat one message in Discord. It can't lose one.
- **Renames.** A post belongs to the GeoGuessr user id, so it survives a rename. Its title follows within about a day of the partner's next message.
- **Typed while the bot is down.** Messages written in a forum post while the bot is offline aren't sent later, since a late reply is worse than none.

[`Documentation/Operations.md`](Documentation/Operations.md) has more on running it.

## Data it stores

A SQLite file holds the ids of processed events for 90 days, friend and club snapshots, player names, and an audit log of actions taken from Discord. Chat text waits there only until it's delivered, and is then removed.

## Development

```bash
dotnet build
dotnet test
scripts/install-git-hooks.sh
```

To run it outside Docker, put your settings in `GeoGuessrAccountShadowBot.Host/appsettings.Local.json`, which is gitignored and copied next to the executable on build. Set `Database:Path` there too, because the default `/data/shadow.db` is a container path. In Rider, use a plain .NET project run configuration: a Docker one can't see this file, since `.dockerignore` keeps it out of the image.

The architecture and how to add an event are in [`Documentation/DeveloperGuide.md`](Documentation/DeveloperGuide.md), and what's known about GeoGuessr's protocol is in [`Documentation/Protocol.md`](Documentation/Protocol.md). See also [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Releases

| Trigger | Result |
|---|---|
| Pull request or push to `main` | Build, tests, formatting, Docker build, CodeQL, dependency review, secret scan |
| Tag such as `1.2.3` | Tests, then `ghcr.io/efibs/geoguessr-account-shadow-bot` and a GitHub Release |

## License

GPL-2.0-or-later. See [`LICENSE`](LICENSE) and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
