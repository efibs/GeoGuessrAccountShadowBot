# Developer guide

## Layout

| Project | Holds | May depend on |
|---|---|---|
| `Domain` | Events, snapshots and their diffs, `Result` | Nothing |
| `Application` | Pipeline, handlers, reconciliation, connection policy, control, ports, options | Domain |
| `Infrastructure` | Socket, HTTP client, EF Core and SQLite | Application |
| `Discord` | Rendering, publishing, listeners, commands | Application |
| `Host` | Composition, logging, health | Everything |
| `Tests` | All tests | Everything |
| `Tools/…Probe` | Protocol investigation | Nothing, on purpose |

Architecture tests enforce these rules.

## How an event flows

```
socket frame ─> FrameDecoder ─┐
                              ├─> EventBus ─> EventProcessor ─> IShadowEventHandler<T> ─> outbox row
HTTP reconciliation ──────────┘                                                              │
                                                       Discord <─ DiscordPublisher <─ OutboxDispatcher
```

- **One consumer.** `EventPipelineWorker` handles events one at a time, which keeps snapshot comparisons free of races.
- **One transaction.** The dedupe key, state changes and outbox rows of an event commit together.
- **Handlers touch the database only.** An architecture test forbids them from taking a network port, because a slow call would stall every event.
- **The outbox is the durability layer.** The queue isn't. Anything lost from the queue is found again by reconciliation, except live-only events.

## Where truth comes from

Log-like data has ids, and state-like data is compared. Frames about state are only doorbells.

| Data | Source | Dedupe |
|---|---|---|
| DMs, club chat | Frames and history | Message id |
| Friendships | `/friendships` diff | Snapshot |
| Invites, account events | Notification list | Notification id |
| Club roster | `/members` diff | Snapshot |
| Ranked | Polled diff | Snapshot |
| Presence, mission board | Frames | None, live only |

The friendship diff is the only producer of friend events. Notification types 1 and 9 only trigger a read, which is why an acceptance can't be announced twice.

## Adding an event

**A new notification type** needs one line in `NotificationClassifier`, and a key if it needs its own switch.

**A new event type:**

1. Add a record in `Domain/Events`, overriding `DedupeKey` if it can repeat, and `SwitchKey` if it can be switched off.
2. If it has a switch, add the key to `EventKeys` and to `appsettings.json`.
3. Produce it, with a line in `FrameDecoder` or in a reconciliation pass.
4. Add one `IShadowEventHandler<T>`. Discovery registers it automatically.
5. Add a snapshot test if it renders something new.

`EventCatalogTests` fails if the record, handler or configuration are out of step.

**A new outbox message** needs a `[JsonDerivedType]` line on `OutboxMessage`, and a case in `DiscordPublisher`.

## Connection handling

`ConnectionPolicy` makes every decision, and has no side effects. `ConnectionSupervisor` carries the decisions out. To change how a close code is treated, change the policy and its table of tests.

## Safety rules

- Text from GeoGuessr travels as `UntrustedText`, and only `CardRenderer` turns it into Discord text.
- `DiscordPublisher` is the only way to Discord, and `MentionPolicy` the only source of allowed mentions. Don't mutate `AllowedMentions.None`.
- Every action as the account goes through `ControlCommandRunner`.
- Sends and changing HTTP calls are never retried.
- DTOs declare only what's used. Responses contain things like club join tokens and device tokens.
- Logs carry ids and codes, never message text.

## Testing

| Kind | Where |
|---|---|
| Pure logic | `Domain`, `Application/ConnectionPolicyTests`, validators, classifier |
| Real frames | `Infrastructure/FrameDecoderTests`, using scrubbed fixtures from the probe |
| Pipeline on real SQLite and migrations | `Application/EventPipelineTests`, `ReconciliationTests`, `ControlTests` |
| Real sockets and HTTP on a local server | `Infrastructure/SocketSessionTests`, `HttpRoundTripTests` |
| Scripted connection with a fake clock | `Infrastructure/ConnectionSupervisorTests` |
| Real object graph | `Host/CompositionTests` |
| Snapshots | `Discord/RenderingTests` |
| Live, read-only | `Infrastructure/LiveGeoGuessrTests` |

The live tests skip unless `GEOGUESSR_LIVE_NCFA` is set, so CI never touches GeoGuessr. Run them after a protocol change:

```bash
GEOGUESSR_LIVE_NCFA=... dotnet test --filter Category=Live
```

Mocks can hide startup failures, as two real bugs in this project showed. Keep the composition and round-trip tests when changing how clients are wired up.

## Database changes

```bash
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project GeoGuessrAccountShadowBot.Infrastructure/GeoGuessrAccountShadowBot.Infrastructure.csproj --output-dir Persistence/Migrations
```

Migrations run at startup.
