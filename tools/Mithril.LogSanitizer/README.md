# Mithril.LogSanitizer

Two-pass PII scrubber for raw PG log captures. Used to produce the L0.5 classifier
test fixtures under `tests/Mithril.Shared.Tests/Logging/Fixtures/`.

## Usage

```
dotnet run --project tools/Mithril.LogSanitizer -- \
    --in scratch/Player-prev.log \
    --out scratch/Player-prev.sanitized.log \
    --source player
```

`--source player` (default) | `chat` — picks the per-source rule set.

## What gets sanitized

- Own character name (from `Logged in as character <name>` banner) → `<CHARACTER>`
- Other player names (from `ProcessAddPlayer(<name>, …)` for Player.log; from
  `[date] [channel] <name>:` for chat) → `<PLAYER_N>` (numbered in first-seen order)
- Windows usernames in stack-trace paths (`C:\Users\<u>\…`) → `<USER>`

Coords, entity IDs, item/NPC/ability names, server identity, and timestamps are
all **left intact** — they're public game state.

## Idempotence

Re-sanitizing already-sanitized output produces identical text. Safe to re-run.
