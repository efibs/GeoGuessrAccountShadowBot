# GeoGuessrAccountShadowBot.Probe

A small tool for finding out how the GeoGuessr realtime socket and API really behave. GeoGuessr documents none of this, so the bot's protocol code should only rest on what this tool has observed.

It has no project or package references, on purpose, so that it can't inherit an assumption from the code it is checking.

## Setup

Create `appsettings.Local.json` in this folder. It is gitignored:

```json
{
  "NcfaToken": "<bare value of the _ncfa cookie>"
}
```

Use an alt account, not your main one. Rebuild after changing the file, because it is copied next to the executable. The `GEOGUESSR_NCFA` environment variable overrides it.

Optional keys are `ClientVersion`, `ApiBaseUrl` and `SocketUrl`.

## Commands

Run from the repository root:

```bash
dotnet run --project Tools/GeoGuessrAccountShadowBot.Probe -- whoami
dotnet run --project Tools/GeoGuessrAccountShadowBot.Probe -- get /api/v3/social/friendships
dotnet run --project Tools/GeoGuessrAccountShadowBot.Probe -- listen --seconds 600
```

Run it with no arguments for the full option list.

## Safety

- **Read-only by default.** Non-GET requests and socket messages other than `Subscribe` and `HeartBeat` need `--allow-write`.
- **Connecting isn't invisible.** `listen` may disconnect other sessions on the account, so it counts as an action even without `--allow-write`.
- **The console shows structure, not content.** It prints codes, topics, sizes and keys. Full bodies and frames go to `captures/`.
- **Captures are private.** They hold real messages and user ids, and `**/captures/` is gitignored.
- **Fixtures go through `scrub`.** It keeps only allow-listed protocol values and replaces ids with stable fakes. Still read the output in `captures/scrubbed/` before committing any of it.
