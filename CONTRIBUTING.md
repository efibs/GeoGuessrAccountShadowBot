# Contributing

Contributions are welcome. Open an issue first for anything large.

## Workflow

1. Branch from `main`, and open a pull request back into it.
2. Run `scripts/install-git-hooks.sh` once, so that formatting and tests run before each commit.
3. Add tests with your change. New events need a handler, a config entry and a snapshot, as described in the [developer guide](Documentation/DeveloperGuide.md).

## Rules

- **Never commit captures or tokens.** Traffic captures hold live session cookies, and the `GeoGuessr source/` and `captures/` folders are gitignored for that reason.
- **Fixtures come from the probe's `scrub` command,** and you still read them before committing.
- **Don't add a dependency under a license that's incompatible with GPL-2.0-or-later.** MediatR 13 and later are excluded for this reason.
- **Keep protocol knowledge first-hand.** Update [Protocol.md](Documentation/Protocol.md) in your own words, and mark what you observed. Don't copy GeoGuessr's code into this repository.

By contributing, you agree that your work is licensed under GPL-2.0-or-later.
