using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mithril.GameState.Tests")]
// #580 — Smaug consumes IPlayerSkillState in tests of its VendorIngestionService
// CivicPride migration; the internal PlayerSkillSnapshot ctor is the simplest
// way for a consumer test to mint a curated snapshot without spinning up a
// real PlayerSkillStateService + log stream. Same trust boundary as
// Mithril.GameState.Tests.
[assembly: InternalsVisibleTo("Smaug.Tests")]
// #726 — Palantir.Tests builds a FakeInventoryView whose Items collection is
// populated with hand-minted InventoryItem rows; the internal ctor + setters
// are the simplest way to seed + mutate per-row state from a consumer test
// without spinning up the full PlayerWorld + ChatWorld composition. Same
// trust boundary as the other consumer-test IVTs above.
[assembly: InternalsVisibleTo("Palantir.Tests")]
