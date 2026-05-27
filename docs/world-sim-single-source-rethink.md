# World-sim — single-source rethink

**Status:** superseded by [`docs/design/arda/l3-dispatch.md`](docs/design/arda/l3-dispatch.md). The critique (merger is wrong for single-source worlds) was ratified and absorbed into the Arda pipeline design. The proposed solution (`IFrameTransform` + preserved folder/composer chain) was subsumed by verb-keyed dispatch to stateful handlers — see the "Relationship to the world-sim rethink" section in the L3 doc. This document is retained as **rationale trail** for why the merger pattern was rejected.

**Original status:** proposal / not yet committed. Captures an architectural critique raised during the debugging session that produced [PR #799](https://github.com/moumantai-gg/mithril/pull/799) (the silent-channel-drop fix in the L0.5 splitter + classifier). Filed here as design rationale; the actionable plan + acceptance criteria + discussion live in [#800](https://github.com/moumantai-gg/mithril/issues/800).

**Companion docs:** read [`world-simulator.md`](world-simulator.md) for the current world-sim architecture and the rationale that landed it. This doc questions one specific structural commitment in that design — the N-way merger inside each world — and proposes replacing it with direct-dispatch over the source's existing total order.

## Trigger

[mithril#799](https://github.com/moumantai-gg/mithril/pull/799) fixed a silent-drop bug in `PlayerLogPipeSplitter.SubscribeWithMarker` (bounded 1024 channel + `DropOldest` evicted ~134 `ProcessAddItem` envelopes during a long-session cold-start). The user-visible symptom was *"area changes are not reaching Palantir or Legolas"*, but the failure chain that explained it ran through the world-sim merger in a way that exposed a deeper structural fragility:

1. `AreaLoadingFrameProducer`'s L1 callback fires for every live `LOADING LEVEL` envelope. Frames are written to its channel correctly.
2. The world merger never dispatches them. Frames sit buffered in the producer's iterator's internal channel forever.
3. Reason: the merger's main loop in [PlayerWorld.cs:127-185](../src/Mithril.WorldSim.Player/PlayerWorld.cs#L127) **sequentially awaits each producer's first `MoveNextAsync` before dispatching any frame**. If any one producer has no head, the entire merger blocks at that `await`.
4. The blocker in the debugging session was `PlayerInventoryFrameProducer` (channel-overflow drops left its iterator with no items to yield). After PR #799 fixes that specific symptom, the **same shape of bug remains** — any producer that is silent during the L1 replay window or during a live idle stretch will starve every other producer in the same world.

`AreaLoadingFrameProducer` is the canonical example: by design, it never emits during the L1 session-start replay window. `LOADING LEVEL` lines are upstream of the session anchor (`ProcessAddPlayer`), so the L1 driver's `FromSessionStart` window doesn't include them. The eager pre-warm in `PlayerAreaWorldRegistration.StartAsync` sidesteps this by applying the seed frame directly to the folder — but it does NOT emit a frame through the producer. Post-PR #799, the merger will reliably reach the area producer's `await` and block there indefinitely until the user portals. Even after a portal unblocks it, every subsequent loop iteration that requires area to have a head will block again until the next portal. State updates from skill, inventory, words-of-power land in *portal-sized bursts*.

This is functional, but it's a foot-gun. The mitigations are real (the area producer's pre-warm, the WordOfPower producer's assumption that PG re-emits `ProcessBook` on login, the inventory producer's reliance on `ProcessAddItem` always firing in replay), but they're each one log-grammar surprise away from wedging.

## Critique

The merger pattern is the natural shape for an N-way ordered merge over **independent streams**. None of the worlds we have today is actually that:

- **PlayerWorld** is single-source: every registered producer is a projection over the same `Player.log` line stream, surfaced through the L1 driver. The L1 driver's source-`Sequence` (byte offset in the source file) is a total order.
- **ChatWorld** is single-source: the chat replay source.
- **Cross-source coordination** (e.g., `InventoryView` correlating PG inventory mutations with chat `[Status]` observations) does NOT happen inside a unified merger. It happens in views above the worlds, each subscribed to its world's bus independently.

So the merger inside each world is doing N-way ordered merge over N projections of a stream that's already totally ordered. The ordering it produces is **identical** to the ordering it would get by just iterating the source in source-order and asking each producer "did you emit anything for this line?". The IAsyncEnumerable abstraction adds a layer that needs cross-producer synchronisation (every producer must have a head before dispatch can proceed) where none is structurally required.

The author of [`world-simulator.md`](world-simulator.md) wrote principle 1 as:

> Frame = `(timestamp, payload)`. The unifying primitive. … **Each world is a timestamp-ordered merger over its N producers.**

That sentence is correct in the abstract — it describes what a world *would* need if its producers came from independent streams. It is the wrong shape for the worlds we actually built. The producers don't need merging; they need dispatching.

## Proposal: direct dispatch

Replace the IAsyncEnumerable-returning `IFrameProducer<T>` with a synchronous transformer:

```csharp
// Replaces IFrameProducer<T>.
public interface IFrameTransform<TEnvelope, TPayload>
{
    Frame<TPayload>? TryTransform(LogEnvelope<TEnvelope> envelope);
}
```

The world's responsibility becomes:

1. Subscribe once to its source stream (L1 for PlayerWorld, chat replay for ChatWorld) per source kind it cares about (LocalPlayer / SystemSignal / Combat / Classified / chat).
2. For each envelope, invoke every registered transformer that wants envelopes of that kind. Each may return 0 or 1 frame.
3. Frames returned go directly to their folder's `Apply`. Folder change events go to the world bus. Composers subscribed to the bus run their pairing logic.
4. Mode flip: read directly off `envelope.IsReplay`. World clock advances per envelope timestamp.

Properties preserved:

- **Replay determinism.** Source order is the authoritative ordering. Producers (now transformers) are pure functions of the envelope; same envelope always yields the same frame. Folders apply frames in source order. Composers see change events in source order.
- **Mode tracking.** Same mechanism (`IsReplay` boundary), same `ModeChanged` bus event.
- **Folder / composer / bus architecture.** Unchanged. The change is strictly in HOW the source becomes frames, not what happens to frames after that.
- **Per-world isolation.** Unchanged. PlayerWorld and ChatWorld each subscribe to their own source.

Properties simplified:

- **No `IFrameProducer<T>.SubscribeAsync`.** No per-producer iterator, no per-producer channel between L1 and the merger, no `ProducerAdapter<T>`, no `ProducerRuntimeState`, no priming, no `await PendingFetch` — the merger's main loop in `PlayerWorld.cs` collapses to "subscribe to source, dispatch per envelope".
- **No `IModeAwareFrameProducer<T>.ReachedLive`.** Mode flips off the envelope's `IsReplay` directly.
- **No "every producer must have a head" invariant.** Silent producers are structurally fine — they simply return null per envelope.
- **No bounded channel between source and producer.** PR #799 is moot under this design (channels exist only at the L0/L0.5 source layers and the L1 driver, where they're the genuine boundary between asynchronous source I/O and synchronous consumers).

Properties lost:

- **Theoretical snapshot/rewind.** The world-simulator doc lists this as "deferred, not blocking" — it's not load-bearing today. Recoverable later at the `world.Apply(envelope)` boundary if a real consumer ever asks.
- **Generic N-way merge over heterogeneous sources.** If we ever genuinely need multi-source merging (e.g., a hypothetical world fused from Player.log + chat + an external timer source), we'd need to reintroduce something merger-shaped — but scoped to where it's actually needed, not as the default shape for every world.

## Risks and open questions

1. **What does "subscribe to source" look like for ChatWorld?** ChatWorld's `ChatLogProducer` reads from a `ChatLogReplaySource` which has its own IAsyncEnumerable surface. The chat side is single-source too but with a different L0/L0.5 shape. The dispatcher needs to handle both.
2. **Where do the L1 driver's containment + diagnostics live?** Today, the L1 driver wraps each handler invocation in try/catch + `ThrottledWarn`, plus `RecordHandlerFailure` for the consecutive-failure SM. That value remains — the dispatcher needs the same per-transform containment. Reusing `LogSubscription<T>` (or its core) for the transformer's source subscription keeps this intact.
3. **Composer BFS resolution.** The current merger has explicit per-frame resolution (folder change events → composers → emitted frames → back through composers). Composers run inline during a frame's dispatch. This stays the same shape; only the source-of-frames upstream changes.
4. **How do we migrate?** The folder / composer / bus interfaces stay stable. The change is producer-shaped: replace `IFrameProducer<T>` (IAsyncEnumerable) with `IFrameTransform<TEnvelope, TPayload>` (synchronous). Five producers across PlayerWorld + ChatWorld. Each migration is one PR; the merger removal lands last when the producer count hits zero. Existing world-sim integration tests (`PlayerAreaWorldIntegrationTests`, `SkillFolderEndToEndTests`, etc.) cover folder behaviour and stay valid; the producer-side tests evolve with each migration.
5. **What about producers that have non-trivial L1 subscription state?** Today's `AreaLoadingFrameProducer.TryBuildSeedFrame` does a reverse-scan of `Player.log` to recover the current area upstream of L1's session-start window. That's external to L1 — it stays as a `world.Apply(seedFrame)` call at world startup, ahead of opening the L1 subscription. The seed isn't a transformer output; it's a one-shot initialisation. This actually becomes cleaner under direct-dispatch — the eager pre-warm becomes a normal startup step instead of a back-compat sidestep around the merger's drain.
6. **What about `WorldClockTickProducer`?** It emits a tick per L1 envelope today, primarily to drive `CalendarTimeAdvanced` for Gandalf's scheduler. Under direct-dispatch, the world's per-envelope dispatch naturally advances the clock — the tick producer collapses into the dispatch loop, with `CalendarTimeAdvanced` emitted by the folder/composer chain on second-resolution boundaries (existing `WorldClockTickFolder` logic, just driven differently).

## Sequencing

If this proposal converges:

1. **Phase 0:** write up the migration ADR-style as a GitHub issue. Pin acceptance criteria (existing folder/composer tests pass, world-sim integration tests pass, no silent silent-producer wedge). Get review before any code moves.
2. **Phase 1:** introduce `IFrameTransform<TEnvelope, TPayload>` alongside `IFrameProducer<T>`. World accepts both during the transition. One PR.
3. **Phase 2..N-1:** migrate each Phase 1+ producer to the transform shape, one PR per producer. Each PR retires its `IFrameProducer<T>` implementation, keeps the folder + composer untouched.
4. **Phase N:** retire `IFrameProducer<T>` + merger + the IAsyncEnumerable adapter machinery. One PR. The world's dispatch loop collapses to the direct-dispatch shape. `WorldClockTickProducer` retires (its function absorbs into the dispatch loop).
5. **Phase N+1:** sweep the world-simulator design doc to reflect the new shape. Update `world-simulator.md` principle 1 to drop "merger over N producers" framing. The principle becomes "world dispatches its source's envelopes through registered transforms in source order."

Throughout: no consumer module changes. Gandalf still subscribes to `CalendarTimeAdvanced`; Legolas still subscribes via `IPlayerWorld.Bus`; views still join PlayerWorld + ChatWorld typed bus channels. The change is structural inside the world boundary, not at its surface.

## What stays

The valuable structural commitments from the world-sim — single-source-per-world (no service spans both Player.log and chat), folder/composer/view separation, the world bus as the canonical cross-cutting surface, mode-aware live-vs-replay semantics, replay determinism — are all preserved. This proposal removes the *merger machinery* that doesn't serve those commitments; it keeps the architectural shape that does.

---

**Open for discussion.** Particularly interested in pushback on:
- Have I missed a producer use-case that genuinely needs N-way merge?
- Is the snapshot/rewind story load-bearing for any near-term roadmap item?
- Migration sequencing — should this land before, alongside, or after the [#700](https://github.com/moumantai-gg/mithril/issues/700) Phase 5 consolidation umbrella?
