# Operations

## Health

The container reports healthy for as long as the bot works and its database answers. It opens no port.

A rejected token or a yielded socket doesn't make it unhealthy, because a restart wouldn't fix either. Both are reported in the status channel instead.

## Status messages

| Message | Meaning | What to do |
|---|---|---|
| First sync finished | Existing data was recorded silently | Nothing |
| GeoGuessr token rejected | The `_ncfa` cookie stopped working | Replace it and restart |
| Yielded the GeoGuessr socket | GeoGuessr replaced the connection | Nothing, it retries by itself |
| GeoGuessr socket blocked | The handshake was refused with a valid token | Check the logs. Mirroring continues over HTTP |
| GeoGuessr client version outdated | GeoGuessr asked this client to reload | Update `GeoGuessr:ClientVersion` |
| GeoGuessr socket down | Reconnecting for over two minutes | Usually an outage on their side |

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

## Backups

The volume holds one SQLite file. Losing it isn't serious: the next start records the current state silently, and forum posts get recreated when a partner next writes.

Use a local volume. SQLite's write-ahead log doesn't work over network file systems. A bind mount must be writable by user 1654.

## Upgrades

Migrations run at startup. Stop the old container first, as only one instance may use a database.

## Client version

`GeoGuessr:ClientVersion` is a site build number, reported as `X-Client` and in the socket URL. How GeoGuessr treats an old value is unknown, so update it if the bot reports it as outdated. The current value is in the `buildId` of the site's page source.
