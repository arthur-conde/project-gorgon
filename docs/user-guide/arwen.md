# Arwen — User Guide

Arwen is the **NPC favor** module: it watches the player log for gift-giving
events, derives per-NPC / per-item / per-keyword favor rates from what you
actually observe in-game, and uses those rates to predict how much favor an
unseen item will produce when given to a specific NPC. It also merges in
community-shared rates as a fallback.

## 1. Get to the panel

Click the **Arwen · Favor** tab (heart icon) in the shell. The panel is
lazy-loaded — the favor ingest service starts the first time you open the
tab and stays running for the rest of the session.

## 2. Tabs

- **Favor Dashboard** — current favor totals and tier per NPC, sourced from
  your character export and live `[Favor]` deltas.
- **Gift Scanner** — paste or query an item; Arwen lists every NPC who likes
  it, the matched preferences, and the predicted favor delta with the tier
  the prediction came from (Item / Signature / NPC / Global).
- **Favor Calculator** — pick an NPC and an item directly to get a single
  estimate.
- **Item Lookup** — reverse direction: given an item, see who values it.
- **Calibration** — the engine that drives all of the above.
- **NPC Dashboard** — per-NPC observation count, tier coverage, and the
  rates Arwen has derived for that NPC.
- **Settings** — calibration source preference (Local / Community / Prefer
  Local) and pending-observation TTL.

## 3. How calibration works

Arwen detects a gift in three log signals, in either order:

1. `ProcessStartInteraction(NPC_Key)` — sets the active NPC.
2. `ProcessDeleteItem(instanceId)` — an item left your inventory while
   talking to that NPC.
3. `ProcessDeltaFavor(NPC_Key, +N)` — favor went up.

When the second of `(2)` and `(3)` lands, Arwen records a **GiftObservation**
and re-derives a rate via:

```
rate = favorDelta / (itemValue × effectivePref × quantity)
```

…where `effectivePref` is the sum of every NPC preference the item matched
and `quantity` is the stack size that left your inventory.

### Specificity hierarchy

When estimating favor for an unseen gift, Arwen walks four tiers in order
from most to least specific:

1. **Item** — keyed by `(NpcKey, ItemInternalName)`.
2. **Signature** — keyed by `(NpcKey, sorted matched-preference set)`.
3. **NPC baseline** — keyed by `NpcKey`.
4. **Global keyword** — last-resort fallback by single keyword.

The first tier with data wins. The estimate-result label tells you which
tier was used.

### Local vs. community rates

The **Source** setting in Settings controls how local and community rates
are merged for each tier:

- **PreferLocal** — your own observations win when present; otherwise fall
  back to community.
- **Community** — community wins when present; otherwise fall back to
  local.
- **Local only** — never use community data.

Community rates are pulled from
[github.com/arthur-conde/mithril-calibration](https://github.com/arthur-conde/mithril-calibration)
and refreshed in the background.

## 4. Stackable items and the pending queue

The game emits **one** `ProcessDeleteItem` for an entire gifted stack — the
event itself doesn't carry the stack size. For non-stackable items
(`MaxStackSize ≤ 1`) Arwen records `quantity = 1` directly. For stackable
items, Arwen tries to look up the stack size from the inventory tracker;
if the tracker doesn't know it (e.g. carryover from a prior PG session
before Arwen was watching), the gift goes to a **pending observation
queue** instead of being persisted with a guessed quantity.

Pending entries:

- Live in memory only — restarting the app drops the list.
- Age out after the configured TTL (default 24h).
- Have to be confirmed (with the actual stack size) or discarded by the
  user. Confirmation promotes the entry into the persisted observation
  list.

## 5. Persistence

Calibration data lives at:

```
%LocalAppData%\Mithril\Arwen\calibration.json
```

The file holds:

- `version` — schema version (currently **3**).
- `observations` — raw gift events.
- `itemRates` / `signatureRates` / `npcRates` / `keywordRates` —
  aggregated rates at each tier.

### Hand-editing the file

This is **safe**, with a few caveats:

- The four `*Rates` blocks are **write-only outputs**. Arwen always
  rebuilds them from `observations` when the file is loaded, so editing or
  deleting individual rate entries has no effect — your changes will be
  overwritten the next time an observation is recorded. Edit the
  `observations` array if you want the change to stick.
- **Edit while the app is closed.** Saves are tmp+rename and there's no
  file watcher; if a new gift lands while you're editing, your changes
  get clobbered.
- **Don't lower `version`.** Re-opening with `version` < 3 triggers
  migration; v2 → v3 *drops every stackable-item observation* because the
  pre-v3 schema had no `quantity` field and over-credited stack rates.
  A `.v{N}.bak` snapshot is written once before migration as a recovery
  path.
- **Don't zero `itemValue`, `quantity`, or all preference `pref` values**
  on an observation — the derived rate becomes 0 and silently weights the
  category average down.
- Keep the JSON well-formed (camelCase, valid shape). A parse failure is
  caught and resets calibration in memory; the file is untouched until
  the next save.

The most common reason to hand-edit is to **fix a wrong stack size** on a
recorded observation — change `quantity` and restart the app; rates will
recompute on load.

## 6. Persistence (settings)

Arwen settings live at `%LocalAppData%\Mithril\Arwen\settings.json` —
calibration source, pending TTL, UI preferences. Calibration data is in
`calibration.json` next to it (see above).

## 7. Tips

- Give a few non-stackable, low-variance items to a fresh NPC first to
  build a per-NPC baseline before relying on the predictor.
- The **Gift Scanner** is the fastest way to see whether a tier is
  pulling from your data or from the community fallback — the result row
  labels the tier.
- If a prediction looks wildly off, check the NPC's observation list in
  the **NPC Dashboard**. A single bad observation (wrong stack size or
  duplicated favor delta) skews the average; deleting it from
  `calibration.json` and restarting fixes it.
- Pending observations don't survive a restart. Confirm or discard them
  before quitting if you care about the data.
