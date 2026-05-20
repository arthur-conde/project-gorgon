# Cross-source correlation — design contract

> **Why this exists.** Several consumers in this repo correlate events that
> arrive on two log streams (Player.log and ChatLogs/) or, more generally,
> from two independent emitters. The hierarchy below — derived in
> [#523](https://github.com/moumantai-gg/mithril/issues/523) and reified by
> the [`PendingCorrelator<TKey,TReq>`](../src/Mithril.Shared/Correlation/PendingCorrelator.cs)
> primitive (#534) and the post-#541 Legolas ingestion migration — exists so
> the next consumer to face this problem doesn't reinvent it, or worse,
> regress to a folk fallback like the pre-#541 "credit at least 1 if the chat
> add never arrived" guess.
>
> Cross-source events from PG's two streams are *comparable* after L0 timestamp
> normalisation (#513) but **not orderable**: both files are written by the
> same game process and sampled at second resolution, so a same-game-second
> pair has no meaningful order in the data itself. The tiers below are about
> *how* to correlate without manufacturing an order the data does not carry.

## The hierarchy (decision tree)

```
Do the two payloads share a join key (item name, instance id, …)?
├─ yes → Tier 1 — keyed correlation (PendingCorrelator).
└─ no → Is it a request/response causal pair (one stream emits a request,
        the other emits the matching response within a bounded window)?
        ├─ yes → Tier 2 — consumer-owned protocol state machine.
        └─ no → Are both streams tailed live by the same process and you
                only need to tiebreak within a shared game-second?
                ├─ yes → Tier 3 — L0 ReadMonotonicTicks (live-only).
                └─ no → Tier 4 — make the consumer order-insensitive.
```

The TTL window that appears in tiers 1–3 is a **correlation gate**, never an
ordering oracle. Two events that fall inside the window are eligible to be
paired; they are not asserted to have happened in a particular order.

## Tier 1 — keyed correlation

**Recognition.** Both payloads carry a shared join key — item name, instance
id, NPC key, survey id. The two sides arrive in either order within a bounded
window and neither pre-knows the other's payload.

**Mechanism.**
[`PendingCorrelator<TKey,TReq>`](../src/Mithril.Shared/Correlation/PendingCorrelator.cs)
from `Mithril.Shared.Correlation`. A bounded multi-map of keyed FIFO buckets
with TTL-based eviction (lazy on `TryTake`, eager via `DrainStale`) and an
explicit `onUnmatched` callback fired for each evicted entry. The side that
arrives first `Add`s; the side that arrives second `TryTake`s.

**In-repo references.**

- **`InventoryService`** ([`src/Mithril.GameState/Inventory/InventoryService.cs`](../src/Mithril.GameState/Inventory/InventoryService.cs))
  fuses Player.log `ProcessAddItem(InternalName(id), …)` (carries the instance
  id but no count) with chat `[Status] X xN added to inventory.` (carries the
  count but no instance id). Keyed by `InternalName`, 5 s TTL
  (`PendingChatTtl`). This is the canonical Tier-1 implementation.
- **`Legolas.Services.LogIngestionService`** ([`src/Legolas.Module/Services/LogIngestionService.cs`](../src/Legolas.Module/Services/LogIngestionService.cs))
  fuses chat `[Status] X xN added to inventory.` with the chat
  `[Status] X collected!` that follows it on a survey collect. Keyed by item
  name, 5 s TTL (matching `InventoryService` deliberately). Unmatched takes
  apply the **credit-0 + throttled `diag.Warn`** policy and skip the
  `CollectedItems` dict write entirely (so the share card omits a `x0` line);
  TTL-evicted noise (skinning/vendor/crafting adds for the same name) is
  surfaced via a `Trace`-level `onUnmatched` callback so post-hoc "why did
  this credit short?" debugging has a trail.

**Pitfall.** Decide the unmatched policy **explicitly** for every consumer.
Silent fallbacks ("credit 1 if nothing matched") manufacture data that wasn't
in the log; an explicit credit-0 + diagnostic surfaces the gap. The pre-#541
Legolas `_pendingAdds` is the cautionary tale — its "credit at least one"
guess looked harmless until the design review forced the question and revealed
it was masking real correlation misses.

## Tier 2 — causal protocol state machine

**Recognition.** The two streams emit a *request* and its *response* within a
bounded window, with **no shared join key** in either payload. The protocol
is request-then-response by construction (the response is *caused by* the
request) — but the response payload says only "here is the answer," not "this
is the answer to your specific request of three seconds ago."

**Mechanism.** A per-consumer state machine, not a shared primitive. Arm an
expectation on each request; consume the first matching response within a TTL
window; on TTL expiry, fire the consumer-defined unmatched policy. If the
player can stack multiple requests in flight (e.g. clicking several treasure
maps in sequence), bind responses **k-th-to-slot-k in arrival order** — see
the `MotherlodeMeasurementCoordinator` "label-agnostic temporal pairing" rule
for the canonical pattern.

**In-repo reference.**
[`Legolas.Services.MotherlodeMeasurementCoordinator`](../src/Legolas.Module/Services/MotherlodeMeasurementCoordinator.cs)
binds `LocalPlayer: ProcessDoDelayLoop("Using <Map>")` (the request, in
Player.log) to `LocalPlayer: ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")`
(the response, also in Player.log, ~1 s offset) — see the Player-Log-Signals
wiki capture ("Motherlode maps → Source") for the same-source emission
evidence. The pair is **same-source via Player.log** (the chat `[Status]` line
is a redundant mirror, [verified](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#source--playerlog-is-canonical-the-chat-mirror-is-redundant)),
which strips the cross-source coupling entirely — a Tier-2 SM operating on
one stream's well-defined `Sequence` ordering, with no second-resolution tie
to break.

**Why no shared primitive.** Tier-2 SMs have heterogeneous state
(per-consumer unmatched policies, batching contracts, response shapes,
timing budgets, undo/redo affordances) and forcing them through a common
base class would either be too thin to be useful or too prescriptive to fit
any of them. Document the *shape* of the pattern here and point at the
reference implementation; do not refactor toward a shared abstraction.

**Pitfall.** Tier-2 SMs are order-sensitive by construction. If PG's
emission ever inverts (response observed before its request), the
implementation MUST surface the inversion observably (warn / credit-0)
rather than silently. The Legolas `LogIngestionService` credit-0 + warn
pattern (a Tier-1 case, but the same discipline applies) is the in-repo
template for "fails cleanly on inversion."

## Tier 3 — live read-order tiebreak

> ⚠️ **Blocked on L1's `IsReplay` flag** ([#511](https://github.com/moumantai-gg/mithril/issues/511)
> deliverable 3, not yet built). This section is a forward-reference
> contract; the mechanism becomes usable when L1 ships.

**Recognition.** Tiers 1 and 2 don't apply — no shared key, no causal
request/response — but you only need to tiebreak the order of two events
*within a shared game-second*. Both streams are tailed live by the same
Mithril process, so the order they were read off disk is a real signal.

**Contract sketch.** L0 mints a high-resolution monotonic
[`ReadMonotonicTicks`](../src/Mithril.Shared/Logging/LogEvent.cs) field on
every emitted line (from `TimeProvider.GetTimestamp()` at tail time). When
L1's forthcoming `IsReplay == false` is set on a line — i.e. the line came
from live tailing, not the session-replay backlog — `ReadMonotonicTicks` is
a usable cross-source tiebreaker within a shared game-second. When
`IsReplay == true` (replay), read-order is meaningless: PG slurped the
backlog file in bulk and no live tailing happened, so the tiebreaker
collapses to nonsense and the consumer must fall back to per-source
`Sequence` + game-second + Tier 1/2 keyed correlation.

**Failure mode.** Read-order is also unreliable when live tailing falls
behind (the [#507](https://github.com/moumantai-gg/mithril/issues/507)
condition). That state is **alarmed and observable**, not silent — when L1
lands, Tier-3 consumers should refuse to tiebreak while #507's lag signal
is set, rather than producing a confidently-wrong ordering.

**In-repo reference.** None yet; pending L1.

## Tier 4 — order-insensitive consumer (the irreducible case)

**Recognition.** None of Tiers 1–3 apply. The events are genuinely
concurrent, keyless, non-causal, and the shared-game-second tiebreaker
either isn't available (e.g. one side is replay) or isn't enough. The data
has no order, period.

**The rule.** The consumer's L3 interpretation MUST NOT depend on the
ordering of cross-source concurrent events. Use idempotent state updates;
prefer set-semantics over sequence-semantics; treat "both happened" as the
only observable, not "A then B" or "B then A".

**Why.** The data has no order. Manufacturing one — by, say, sorting on
the L0 timestamp (which collides at second resolution) and falling back to
file-of-origin as a deterministic tiebreaker — produces a stable but
**fabricated** ordering. That ordering will look correct in tests written
against the same fabrication and silently wrong in production.

**Example pattern.** If two pets were summoned in the same game-second and
the consumer needs to render "currently summoned" status, record both as
present rather than ordering them. If the consumer needs an order for a
ledger row, take the order from a separate authoritative source (the
chronological log of explicit user actions, for example) rather than
synthesising one from log timestamps.

**In-repo reference.** None — no consumer currently falls in this tier.
If one ever does, this section gets the first reference.

## Consumer-to-tier mapping

| Consumer | Tier | Notes |
|---|---|---|
| `InventoryService` (`ProcessAddItem` ↔ chat `[Status] added`) | 1 | Keyed by `InternalName`, 5 s TTL |
| `Legolas.Services.LogIngestionService` (chat `added` ↔ chat `collected!`) | 1 | Keyed by item name, 5 s TTL; credit-0 + warn on unmatched takes; Trace on TTL eviction (post-#541) |
| `Legolas.Services.MotherlodeMeasurementCoordinator` (`ProcessDoDelayLoop` ↔ `ProcessScreenText`) | 2 | Same-source via Player.log; k-th-to-slot-k binding; label-agnostic temporal pairing |
| *(future Tier-3 consumer)* | 3 | Awaits L1 (#511 deliverable 3) |

## Open questions / future work

- **L1 / `IsReplay`** lands (#511 deliverable 3) → Tier-3 implementation
  becomes possible; the first Tier-3 consumer reference moves into the
  mapping table above.
- **First Tier-4 consumer**, if one ever surfaces, gets the first reference
  in §Tier 4.
- **PG's ADD-before-COLLECT emission order** across the survey vocabulary is
  pending its own wiki capture
  ([#540](https://github.com/moumantai-gg/mithril/issues/540)) — the Tier-1
  and Tier-2 references above will link to it once it lands.
