# Port NpcNameResolver to a shared IEntityNameResolver DI service

**Tracked in:** #282. Companion to cookbook [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md), which already describes `IEntityNameResolver` as the target shape and names `NpcNameResolver` as the transitional artifact this PR removes.

> **Read first:** the cookbook's *Scaffolding checklist* step 1 (no per-kind static helpers) and *Cross-link chips → audit existing surfaces* sub-section. They describe the end state. This handoff is the migration to get there.

## Context

The cookbook's name-resolver guidance currently points at a shared `IEntityNameResolver` that doesn't exist yet, with a transitional sentence acknowledging that NPCs ship through a `NpcNameResolver` static. That sentence comes out as part of this PR's acceptance.

The issue body at [#282](https://github.com/moumantai-gg/mithril/issues/282) is already detailed (interface sketch, scope, acceptance criteria). This handoff pins the file/line refs and flags the decision points so the session can drive without re-grepping.

## Approach — port, don't parallel-build

`NpcNameResolver` has four call sites, all in `Silmarillion.Module`. There's no value in running a parallel-stack period. One PR: introduce the service, migrate the four sites, delete the static. No deprecation deadline, no dual-stack burden.

## Files to create

### `src/Mithril.Shared/Reference/IEntityNameResolver.cs`

```csharp
namespace Mithril.Shared.Reference;

public interface IEntityNameResolver
{
    /// <summary>
    /// Returns the friendly display name for an entity reference, falling back through
    /// the kind's POCO Name field → internal-name prefix-stripping → raw internal name.
    /// </summary>
    string Resolve(EntityRef reference);
}
```

Lives in `Mithril.Shared.Reference` (not `Mithril.Shared.Wpf` — the resolver is pure text logic with no WPF dependency). Internal-only impl + public interface keeps the contract small.

### `src/Mithril.Shared/Reference/ReferenceDataEntityNameResolver.cs`

```csharp
internal sealed class ReferenceDataEntityNameResolver : IEntityNameResolver
{
    private readonly IReferenceDataService _refData;
    public ReferenceDataEntityNameResolver(IReferenceDataService refData) => _refData = refData;

    public string Resolve(EntityRef r) => r.Kind switch
    {
        EntityKind.Item   => ResolveItem(r.InternalName),
        EntityKind.Recipe => ResolveRecipe(r.InternalName),
        EntityKind.Npc    => ResolveNpc(r.InternalName),
        _                 => r.InternalName,
    };

    private string ResolveItem(string internalName) =>
        _refData.Items.Values.FirstOrDefault(i => i.InternalName == internalName)?.Name
            ?? internalName;

    private string ResolveRecipe(string internalName) =>
        _refData.RecipesByInternalName.TryGetValue(internalName, out var r) && !string.IsNullOrEmpty(r.Name)
            ? r.Name!
            : internalName;

    private string ResolveNpc(string internalName) =>
        _refData.NpcsByInternalName.TryGetValue(internalName, out var npc) && !string.IsNullOrEmpty(npc.Name)
            ? npc.Name!
            : StripNpcPrefix(internalName);

    private static string StripNpcPrefix(string internalName) =>
        // mirror the existing NpcNameResolver.StripNpcPrefix logic exactly —
        // confirm the prefix list against npcs.json envelope keys before deleting the original.
        internalName.StartsWith("NPC_") ? internalName[4..] : internalName;
}
```

**Item resolution gotcha:** `IReferenceDataService.Items` is keyed by numeric `ItemCode`, not `InternalName`. The sketch above scans `Values.FirstOrDefault` which is O(n) — acceptable for one-off chip resolution but not great. Two options:

1. Add `IReferenceDataService.ItemsByInternalName` (sibling to `RecipesByInternalName`) if it doesn't already exist. Cleaner.
2. Cache an internal-name lookup inside `ReferenceDataEntityNameResolver` keyed off the resolver's lifetime.

Check whether `ItemsByInternalName` already exists before adding it. If not, prefer option 1 — it's plumbing other consumers (cross-link sections) likely want too.

### DI registration

Find where `IReferenceDataService` is registered (likely `ShellServiceCollectionExtensions.AddMithrilShared` or similar — grep for `AddSingleton<IReferenceDataService` to find the call site). Add right after it:

```csharp
services.AddSingleton<IEntityNameResolver, ReferenceDataEntityNameResolver>();
```

Same lifetime as the underlying refData. Resolver is stateless beyond the injected refData reference, so singleton is correct.

## Migrate the four call sites

All in `src/Silmarillion.Module/ViewModels/`. Inject `IEntityNameResolver` via constructor on each consuming VM, store as `_nameResolver`, replace the static calls:

| Site | Current | Replacement |
|---|---|---|
| [ItemsTabViewModel.cs:268](../../src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs#L268) | `NpcNameResolver.Resolve(_refData, s.Npc!)` | `_nameResolver.Resolve(EntityRef.Npc(s.Npc!))` |
| [NpcDetailViewModel.cs:42](../../src/Silmarillion.Module/ViewModels/NpcDetailViewModel.cs#L42) | `NpcNameResolver.StripNpcPrefix(InternalName)` | `_nameResolver.Resolve(EntityRef.Npc(InternalName))` (resolver does the strip internally) |
| [NpcsTabViewModel.cs:120-121](../../src/Silmarillion.Module/ViewModels/NpcsTabViewModel.cs#L120-L121) | `NpcNameResolver.StripNpcPrefix(internalName)` | same as above |
| [RecipesTabViewModel.cs:264](../../src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs#L264) | `NpcNameResolver.Resolve(_refData, s.Npc!)` | `_nameResolver.Resolve(EntityRef.Npc(s.Npc!))` |

Each consumer VM's constructor gains an `IEntityNameResolver nameResolver` parameter. **This ripples to tests** — `ItemsTabViewModelTests`, `RecipesTabViewModelTests`, `NpcsTabViewModelTests`, `NpcDetailViewModelTests` all instantiate the VMs directly and will need a stub resolver. The cookbook's *"Use named arguments throughout to keep the diff readable"* guidance applies here.

## Delete `NpcNameResolver`

[src/Silmarillion.Module/ViewModels/NpcNameResolver.cs](../../src/Silmarillion.Module/ViewModels/NpcNameResolver.cs) — delete the file once all four call sites are migrated. Confirm no other references remain via grep on `NpcNameResolver` before deleting.

## Tests

### New: `tests/Mithril.Shared.Tests/Reference/ReferenceDataEntityNameResolverTests.cs`

Cover each kind's fallback chain. Three tests per kind suffice:

- POCO `Name` present → returned verbatim
- POCO `Name` null/empty (or POCO absent from refData) → fallback applies (prefix-strip for NPCs, raw InternalName for Items/Recipes)
- Unknown `EntityKind` (e.g. `EntityKind.Quest` before its case is added) → returns `reference.InternalName`

Use a minimal `StubReferenceData` fixture; mirror the shape of `tests/Mithril.Shared.Tests/Reference/Phase7Fixture.cs` or similar small test fakes.

### New: shared `FakeEntityNameResolver` test helper

In `tests/TestSupport/` (or wherever shared module-test infra lives — check existing test fakes' home):

```csharp
public sealed class FakeEntityNameResolver : IEntityNameResolver
{
    private readonly Dictionary<EntityRef, string> _map;
    public FakeEntityNameResolver(params (EntityKind kind, string internalName, string friendly)[] entries) =>
        _map = entries.ToDictionary(e => new EntityRef(e.kind, e.internalName), e => e.friendly);
    public string Resolve(EntityRef reference) =>
        _map.TryGetValue(reference, out var s) ? s : reference.InternalName;
}
```

Lets per-VM tests assert resolved-name behaviour without spinning up `StubReferenceData` + a real resolver. Use across `NpcsTabViewModelTests`, `NpcDetailViewModelTests`, `RecipesTabViewModelTests`, `ItemsTabViewModelTests`.

### Existing tests touched

The four VM test files each need the new constructor parameter wired through their existing fixtures. For most assertions that don't depend on the resolved string, `new FakeEntityNameResolver()` (empty map → InternalName fallback) is fine. For assertions that *do* check display names, pass the relevant `(kind, internal, friendly)` tuple.

## Cookbook update — bundled in this PR

The cookbook's *Cross-link chips → audit existing surfaces* sub-section currently reads:

> Replace the friendly display name in those builders with `_nameResolver.Resolve(EntityRef.<NewKind>(internalName))` from the shared `IEntityNameResolver` (per scaffolding step 1). Until that service exists, NPCs ship through a transitional `NpcNameResolver` static at [src/Silmarillion.Module/ViewModels/NpcNameResolver.cs](../src/Silmarillion.Module/ViewModels/NpcNameResolver.cs); fold it into the shared service when you wire your kind in.

Trim the second sentence. The static is gone after this PR; the transitional note is meaningless.

## Verification

1. `dotnet build Mithril.slnx` — warnings-as-errors clean. No stragglers referencing `NpcNameResolver`.
2. `dotnet test Mithril.shared.Tests --filter ReferenceDataEntityNameResolver` — new tests pass.
3. `dotnet test tests/Silmarillion.Tests` — all VM tests green with the new constructor parameter wired through.
4. `dotnet test Mithril.slnx` — full suite green. Pay attention to Bilbo / Arwen / Celebrimbor / Smaug; the new `IEntityNameResolver` is in their reachable DI graph (they take `IReferenceDataService` and the new service piggybacks on the same registration site) but they shouldn't notice.
5. Manual smoke: open Silmarillion → Items tab → pick an item with NPC vendors in Sources; confirm the "Vendor: Joeh" chip still reads the friendly name (not `NPC_Joeh`). Same on Recipes tab "Taught by" section. Same on NPCs tab list rows and detail header. Mithril-build-file-lock convention applies (close the app between rebuilds).

## Out of scope

- Pre-extending the resolver to Ability/Effect/Quest/Area/etc. Each Bucket B session adds its own switch case as part of that tab's PR. Resolver grows with the tabs.
- Localization / `strings_all` lookup — defer to #265 (typed `StringRef` pattern). When #265 lands, the resolver becomes the natural injection point for the strings table.
- Caching beyond what the implementation needs. The Item-by-InternalName lookup may want internal caching depending on which option you pick above; nothing else does.
- Cross-module consumers. Arwen / Bilbo currently consume `NpcEntry` / `Item` directly; let demand pull the resolver into those modules if it materialises.

## Commit / PR shape

Single PR against `main`. Suggested branch: `chore/282-entity-name-resolver`. Conventional commit:

```
chore(silmarillion): port NpcNameResolver to shared IEntityNameResolver — #282
```

Likely diff size: ~250–400 lines (interface + impl + 4 VM constructor updates + 4 test-file updates + new resolver tests + test helper + cookbook trim − the deleted `NpcNameResolver.cs`).

Closes #282. Does not close any Bucket B tab issue — but unblocks the cleaner injection pattern the next tab session (probably Quests, #242) will rely on.
