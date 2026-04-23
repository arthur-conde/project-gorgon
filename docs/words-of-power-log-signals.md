# Words of Power — Player.log signals

Reference notes for what the game emits to `Player.log` and `ChatLogs/` around
Word-of-Power (WoP) events. Captured in April 2026 against the live client.

Saruman (the module that tracks WoPs) uses only a small slice of these
signals today — the discovery ProcessBook and chat-log utterances. The rest
is documented here so future work (e.g. tier tagging, scroll-inventory
watching, effect expiry tracking) can start from facts rather than
guesswork.

## Discovery book

Every WoP discovery fires a `ProcessBook` line in `Player.log`:

```
[02:02:58] LocalPlayer: ProcessBook(
  "You discovered a word of power!",
  "You've discovered a word of power: <sel>FEAVEG</sel>Speak it out loud for this effect:\n\n
   <b><size=125%>Word of Power: Fast Swimmer</size></b>\n
   Speaking this word makes you swim much faster for five minutes.\n\n
   <i><size=75%>Words of Power can only be spoken once, then they lose their power. ...</size></i>",
  "", "", "", True, False, False, False, False, "")
```

Key properties:

- Title is always exactly `"You discovered a word of power!"` — a reliable
  regex anchor.
- Body uses literal `\n` (backslash + n, two characters) for line breaks —
  **not** real newlines, because the whole body is one `ProcessBook` string
  argument on a single log line.
- Groups inside the body: `<sel>CODE</sel>`, `<b><size=125%>Word of Power:
  EFFECT</size></b>`, then description on the next `\n`-delimited line.
- Codes are uppercase alpha only, 6–11+ characters observed.

## Craft flows

Two recipe flavours exist for each tier: direct-craft and scroll-craft.

### Direct-craft

Recipes whose `InternalName` is just `WordOfPower{N}` (`recipe_801`, `811`,
`821`, `831`, `841`, `846`). Crafting these produces the discovery
immediately. Captured at lines 807354–807415 of the April 22 log:

```
[02:02:56] ProcessBook("You discovered a word of power!", "...<sel>ZOCKZECH</sel>...")
[02:02:56] ProcessUpdateItemCode(...)
[02:02:56] ProcessUpdateItemCode(...)
[02:02:56] ProcessAddItem(EmptyBottle(...), -1, True)
[02:02:56] ProcessUpdateRecipe(801, 2)
```

ProcessBook and ProcessUpdateRecipe share a timestamp and are separated by
≤5 bookkeeping lines.

### Scroll-craft

Recipes whose `InternalName` is `WordOfPower{N}Scroll` (`recipe_802`, `812`,
`822`, `832`, `842`). Crafting these deposits a **scroll item** into the
player's inventory — no ProcessBook fires yet. Captured at line 406739 of
the April 22 log:

```
[14:35:55] ProcessUpdateItemCode(73611376, 463754, True)
[14:35:55] ProcessDeleteItem(91629814)
[14:35:55] ProcessUpdateItemCode(98534875, 201618, True)
[14:35:55] ProcessAddItem(DiscoverWordOfPower1(104675266), -1, True)   ← the scroll
[14:35:55] ProcessUpdateItemCode(104653994, 327681, True)
[14:35:55] ProcessUpdateRecipe(802, 1)
[14:35:55] ProcessUpdateSkill({type=Lore,raw=13,...})
```

The scroll item's `InternalName` is `DiscoverWordOfPower{N}` where `N`
encodes the tier (1 = 2-syllable, 2 = 3-syllable, …, 6 = 7-syllable).

### Scroll-read

Later (possibly in another session), the player uses the scroll item. The
read fires the ProcessBook *and* a matching ProcessDeleteItem for the same
instance ID. Captured at lines 409034–409036:

```
[14:40:26] ProcessBook("You discovered a word of power!", "...<sel>XXYDMIRR</sel>...Word of Power: Fear of Water...")
[14:40:26] 16516.73: Playing sound SkillBook_Reading
[14:40:26] ProcessDeleteItem(104675266)    ← same instanceId as the earlier AddItem
```

No `ProcessUpdateRecipe` fires on the read. The tier signal for scroll-read
discoveries lives in the scroll item's name observed at craft time, not in
the discovery line itself.

Order within the timestamp: ProcessBook precedes ProcessDeleteItem in the
one sample captured. The game client could plausibly flip this; any
correlation logic should tolerate both orderings.

## Recipe ID ↔ tier map (from recipes.json)

| Recipe ID | InternalName         | Syllables | Tier |
| --------- | -------------------- | --------- | ---- |
| 801       | WordOfPower          | 2         | 1    |
| 802       | WordOfPowerScroll    | 2         | 1    |
| 811       | WordOfPower2         | 3         | 2    |
| 812       | WordOfPower2Scroll   | 3         | 2    |
| 821       | WordOfPower3         | 4         | 3    |
| 822       | WordOfPower3Scroll   | 4         | 3    |
| 831       | WordOfPower4         | 5         | 4    |
| 832       | WordOfPower4Scroll   | 5         | 4    |
| 841       | WordOfPower5         | 6         | 5    |
| 842       | WordOfPower5Scroll   | 6         | 5    |
| 846       | WordOfPower6         | 7         | 6    |

IDs are not a clean range — `846` breaks the `84X` pattern — so any future
code that needs this mapping should build it from
`IReferenceDataService.Recipes` by regex on `InternalName`, not hardcode.

## Recipe use counter

`ProcessUpdateRecipe(recipeId, count)` fires every time the player crafts
that recipe. The `count` is cumulative "times I've run this recipe". Over
the five April 22 discoveries, the recipe-801 counter went 2 → 3 → 4 → 5 →
6. Not directly useful for Saruman today, but a consistent signal.

## Speaking a Word of Power (chat)

Speaking a WoP produces a plain chat-log line like any other utterance:

```
26-04-22 02:39:57	[Nearby] Hikaratu: PRYSGWIMLIK
```

- Format: `YY-MM-DD HH:MM:SS\t[Channel] Speaker: message`.
- The code appears verbatim in the message; there is no server-side event
  distinguishing "said aloud to activate an effect" from "pasted into guild
  chat as trivia." Saruman deliberately accepts any channel, leaving the
  caller to mark a row back to Known if a paste triggers a false positive.
- No `Player.log` event accompanies the speak; the in-game buff indicator
  is the only feedback.

## Effect catalog

Every discovery carries an **effect name** and a description. Effects are
not unique to a code — multiple codes can produce "Anemia", "Fear of
Water", etc. Multi-player sharing: any player who has discovered a given
code can speak it; the mapping code → effect is a shared global fact, so
once a code is heard in chat it's usually game-over for that code across
anyone who learned it.

Known effects observed in captured logs:

- Anemia — attacks cost +5 Power, 5 min
- Fear of Water — breath depletes at 5× rate underwater, 5 min
- Fast Swimmer — swim faster, 5 min
- Increased Inventory — +10 inventory slots, 1 hour
- Cure Bovinity — stop being a cow if you are one
- Weak Max-Power Boost — raises maximum Power
- Hold Your Breath — doubles lung capacity
- Teleport to Serbule Crypt
- Instant Death
- Weak Life Regeneration

This list is not exhaustive — the in-game list is longer. The module
doesn't need a catalog because the game ships the effect text inside the
discovery ProcessBook body itself.
