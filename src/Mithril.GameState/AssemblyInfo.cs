using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mithril.GameState.Tests")]
// #580 — Smaug consumes IPlayerSkillState in tests of its VendorIngestionService
// CivicPride migration; the internal PlayerSkillSnapshot ctor is the simplest
// way for a consumer test to mint a curated snapshot without spinning up a
// real PlayerSkillStateService + log stream. Same trust boundary as
// Mithril.GameState.Tests.
[assembly: InternalsVisibleTo("Smaug.Tests")]
