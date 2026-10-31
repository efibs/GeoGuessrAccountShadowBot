# GeoGuessr protocol notes

GeoGuessr doesn't document any of this. These notes are in our own words, and each item says how it's known.

- **Observed** means seen live with the probe, in October 2026.
- **From the client** means read from the site's client code, and not yet seen live.

## Socket

**Observed:**

- `wss://api.geoguessr.com/ws?c=<client>&tabId=<uuid>&attempt=1&visibility=visible`
- The `_ncfa` cookie and the site's Origin are enough. Cloudflare accepts a plain .NET client with an honest User-Agent.
- There's no subprotocol, and frames are JSON text.
- The server answers WebSocket pings. It doesn't answer the application `HeartBeat`.
- A browser and several bot connections coexist on one account, even with the same `tabId`.

**From the client:** the site adds `reconnect=true` after close code 1012, and sends `{"code":"HeartBeat"}` every 15 seconds.

### Frames

Outbound:

```json
{"code":"Subscribe","topic":"self:<userId>","client":"web"}
{"code":"ChatMessage","topic":"chat:Friend:TextMessages:<peerId>","payload":"text","client":"web"}
```

Inbound frames have `code`, `topic`, `payload`, `accessToken`, `client` and `timestamp`. The `payload` is a JSON document encoded as a string, or null. The bot never reads `accessToken`.

### Topics

| Topic | Purpose | Status |
|---|---|---|
| `self:<userId>` | Friends, presence, notifications | Observed, sends no ack |
| `chat:Friend:TextMessages:<userId>` | DM inbox | Observed, acked with `Subscribed` |
| `chat:Friend:TextMessages:<peerId>` | Sending a DM | Observed |
| `club:<clubId>`, `chat:Club:TextMessages:<clubId>` | Club | From the client |

A repeated `Subscribe` to `self:` is harmless, and sends nothing back.

### Codes

| Code | Payload | Status |
|---|---|---|
| `Subscribed` | Has `level` | Observed |
| `ChatMessage` | See below | Observed |
| `NewFriendRequest`, `FriendsUpdated`, `NotificationUpdate` | Null | Observed |
| `FriendCameOnline`, `FriendWentOffline` | `{friendId}` | Observed |
| `SubscribeDenied`, `BadgesUnlocked`, `ClubMember*`, `MissionBoardProgressed` | Varies | From the client |

Presence flickers: offline and online can arrive within a second of each other on a page reload.

An outgoing friend request sends no frame. Accepting, declining or being accepted sends `FriendsUpdated` and `NotificationUpdate`, and an unfriend sends `FriendsUpdated` alone.

### Close codes

All from the client. Code 4100 could not be provoked, not with a browser, a second connection or a reused `tabId`, so what triggers it is unknown.

| Code | Meaning |
|---|---|
| 1012 | Server restart |
| 4100 | Superseded |
| 4200 | Client should reload |
| 1001 `Unauthorized` | Session rejected |

## Chat

**Observed:**

- Message ids are identical on the socket and in the history, and are 10 characters long. They're combined with the room id for dedupe.
- `sentAt` differs in precision between the two, so it can't be part of a key.
- The socket has `channel` and `reactionPayload`, and the history has `reactions` and `isBlocked`.
- Rooms and histories list messages newest first.
- `roomId` is the two user ids, joined with a colon.
- Sent messages come back on the inbox of every connection within about 40 ms, including those sent from a browser.
- **Text longer than 300 characters is silently cut to 300.**
- Text is otherwise stored unchanged, including whitespace, HTML and `@everyone`.
- A DM delivered to a connected socket isn't counted as unread, so `/chat/unreads` can't be used to find missed messages. A DM sent while nothing is connected does appear there.
- `friendChatRooms` in the friends summary lists rooms with their messages, whatever their unread state.
- Reading history doesn't clear the unread state.

## HTTP

All under `https://www.geoguessr.com`, with the `_ncfa` cookie.

| Request | Purpose | Status |
|---|---|---|
| `GET /api/v3/profiles` | Own id in `user.id` | Observed |
| `GET /api/v3/social/friendships` | Friends and requests | Observed |
| `GET /api/v3/social/friends/summary?fast=true&page=<n>` | Rooms, in `friendChatRooms` | Observed |
| `GET /api/v4/notifications?count=<n>&page=<n>` | Notifications | Observed |
| `GET /api/v4/chat/unreads` | Unread rooms, keyed by partner | Observed |
| `GET /api/v4/chat/<peerId>` | History | Observed |
| `GET /api/v4/player-identities/<userId>` | Name, in `player.nick` | Observed |
| `GET /api/v4/clubs/my` | Own club, 204 without one | Observed without a club |
| `GET /api/v4/ranked-system/progress/<userId>` | Division | Observed on an unranked account |
| `GET /api/v4/season-pass/me` | Season id | Observed |
| `POST /api/v3/social/friends/<id>?context=` | Sends a request | Observed |
| `DELETE /api/v3/social/friends/<id>` | Declines a request | Observed |
| `PUT /api/v3/social/friends/<id>?context=` | **Accepts a request** | From the client |
| `GET /api/v4/clubs/<id>/members`, `/chat` | Club | From GeoClubBot and the client |

`GET /api/v4/ranked-v2/me` answered 404. Reading notifications doesn't mark them as read. Their `payload` was empty for the friend types.

**Declining is lasting.** A declined person can't send another request, and gets `Your previous friend request has been declined.`. This stays true after they become friends through a request from the other side and then unfriend. The declining side can still send a request.

## Not yet verified

- Accepting an incoming request with `PUT`.
- Everything about clubs, because the test account isn't in one.
- What triggers close code 4100, and what signing out does to a live socket.
- Reply delivery when an archived forum post is reopened after a restart.
