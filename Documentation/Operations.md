# Operations

## Health

The container reports healthy for as long as the bot works and its database answers. It opens no port, unless `ExternalApi:Enabled` is on.

A rejected token or a yielded socket doesn't make it unhealthy, because a restart wouldn't fix either. Both are reported in the status channel instead. An [external source](ExternalSources.md) that has been down for its `UnhealthyAfter`, 30 minutes by default, does make it unhealthy.

## Status messages

| Message | Meaning | What to do |
|---|---|---|
| First sync finished | Existing data was recorded silently | Nothing |
| GeoGuessr token rejected | The `_ncfa` cookie stopped working | Replace it and restart |
| Yielded the GeoGuessr socket | GeoGuessr replaced the connection | Nothing, it retries by itself |
| GeoGuessr socket blocked | The handshake was refused with a valid token | Check the logs. Mirroring continues over HTTP |
| GeoGuessr client version outdated | GeoGuessr asked this client to reload | Update `GeoGuessr:ClientVersion` |
| GeoGuessr socket down | Reconnecting for over two minutes | Usually an outage on their side |
| A background worker crashed | A part of the bot failed and restarts by itself | Nothing if it's rare. If it repeats, the logs say why |
| Source … unreachable | An [external source](ExternalSources.md) has been down for its `AlertAfter` | Check the other instance. Its events are caught up once it's back |
| Source … refused this instance | The source doesn't accept the token, or allows nothing | Compare `Sources:<name>:Token` here with `ExternalApi:Clients` there |
| Events from … may be missing | This instance was away longer than the source keeps events | Nothing, but rules may have missed some events |
| Source … started over | The source's feed is behind what this instance saw | Nothing, if its database was replaced on purpose |

A headless instance has no status channel. It writes these messages and automation reports to its log.

Brief reconnects are deliberately silent.

## Replacing the token

Update the secret and restart. The token must belong to the same account, because the bot refuses to start on a database of another account. To shadow a different account, use a new volume.

## Reactions on replies

| Reaction | Meaning |
|---|---|
| ✅ | Delivered |
| ⚠️ | Sent, but GeoGuessr didn't confirm it |
| ❌ | Not sent, because the bot wasn't connected |
| 🚫 | Refused, with the reason in a reply |

## Automations

To stop one rule, run `/automation disable <name>`, which also cancels its unfinished runs. To stop them all, set `Control:Automation:Enabled` to `false` and restart. Waiting runs then stay in the database, and continue when automations are switched back on, unless they've grown older than their rule allows.

A run that fails is reported in the report channel with every step it took. `/automation runs` lists recent runs, and the audit log records each action as `auto:<rule>:<action>`. See [`Automations.md`](Automations.md).

## Linked instances

To connect two instances, see [Headless mode and external sources](ExternalSources.md). To cut a client off, remove it from `ExternalApi:Clients`, or change its token, and restart. Each source's position in the other's feed is kept in the database, so a reader that gets a new volume starts again from the source's newest event.

## Backups

The volume holds one SQLite file. Losing it isn't serious: the next start records the current state silently, and forum posts get recreated when a partner next writes.

Use a local volume. SQLite's write-ahead log doesn't work over network file systems. A bind mount must be writable by user 1654.

## Upgrades

Migrations run at startup. Stop the old container first, as only one instance may use a database.

## Client version

`GeoGuessr:ClientVersion` is a site build number, reported as `X-Client` and in the socket URL. How GeoGuessr treats an old value is unknown, so update it if the bot reports it as outdated. The current value is in the `buildId` of the site's page source.
