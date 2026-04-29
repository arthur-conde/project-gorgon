# Plan: `tools/RefreshAndValidate` — pre-merge CDN drift detector

## Goal

Catch Project Gorgon CDN schema drift *before* the bundled JSON copy in
`src/Mithril.Shared/Reference/BundledData/` is updated, instead of after a
user's running Mithril copy crashes mid-refresh. A standalone .NET console
tool fetches every BundledData file from the live CDN, runs the same
validation gates the test harness already uses
([`BundledDataValidationTests`](../../tests/Mithril.Reference.Tests/Validation/BundledDataValidationTests.cs)),
and exits non-zero on drift. Run it from CI on a schedule (or before
bumping bundled data) to fail loud when Elder Game ships a new
discriminator value the model layer doesn't recognise.

## Why this is worth building

The Phase 6 instrumentation
([`ReferenceDataService.ReportUnknowns`](../../src/Mithril.Shared/Reference/ReferenceDataService.cs))
catches drift at *runtime* on the user's machine — by then the broken
model is already shipped. The
[Phase 1 validation harness](../../tests/Mithril.Reference.Tests/Validation/BundledDataValidationTests.cs)
gates only the *bundled* JSON checked into the repo. The gap is the live
CDN between bundled refreshes. This tool closes that gap.

## Design choice: dotnet console tool, not a Node CLI

The user asked whether this should be a Node tool living in `pg-data-mcp`.
Earlier conversation (the message thread that produced this plan)
considered three options: Node CLI in `pg-data-mcp`, dotnet tool in this
repo, or a CI-only bash workflow. Recommendation landed on **dotnet
console tool in `tools/RefreshAndValidate/`**:

- The validation harness is C# already; tool stays in one toolchain.
- The `tools/` folder pattern is established
  ([`tools/XamlResourceLint`](../../tools/XamlResourceLint)).
- The tool reuses
  [`Mithril.Reference.ParserRegistry.Discover()`](../../src/Mithril.Reference/ParserRegistry.cs)
  and
  [`Mithril.Shared.Reference.CdnVersionDetector`](../../src/Mithril.Shared/Reference/CdnVersionDetector.cs)
  directly — no env-var plumbing into the test runner, no Node sidecar.

If this assumption is wrong (e.g. the team would rather keep all
"scrape-live-CDN" work in one Node repo), reconsider before building.

## Scope

**In:**

1. Console app at `tools/RefreshAndValidate/`.
2. Fetches every file in `ParserRegistry.Discover().Select(s => s.FileName)`
   from `https://cdn.projectgorgon.com/{version}/data/{file}.json`, where
   `{version}` comes from `CdnVersionDetector.TryDetectAsync`.
3. Writes fetched JSONs to a temp directory; cleans up on exit.
4. For each `IParserSpec`: parse, count entries, walk for unknowns.
5. Prints a summary table to stdout.
6. Exit code: `0` if zero unknowns and entry counts ≥ each spec's
   `MinimumEntryCount`; `1` otherwise.
7. CI workflow at `.github/workflows/cdn-drift-check.yml` running the
   tool on a daily cron.

**Out (deferred — call out as future work in commit message):**

- Auto-updating `src/Mithril.Shared/Reference/BundledData/` from a
  successful run. Useful but adds scope: a successful refresh would
  generate a PR that bumps the bundled JSON, which needs separate
  reviewer policy. Punt to a follow-up "auto-bundled-data-refresh"
  workflow.
- Field-coverage walker (compare every `JObject` property name to POCO
  declarations). Documented as future work in
  [mithril-reference-roadmap.md](../mithril-reference-roadmap.md#phase-6).

## Step-by-step

### 1. Project skeleton

Create `tools/RefreshAndValidate/RefreshAndValidate.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Mithril.Tools.RefreshAndValidate</RootNamespace>
    <IsPackable>false</IsPackable>
    <!-- Same opt-out as XamlResourceLint: this tool runs against the live
         CDN, not the WPF app, and the VS-threading analyzer warnings don't
         apply. -->
    <NoWarn>$(NoWarn);CA1416</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Mithril.Reference\Mithril.Reference.csproj" />
    <ProjectReference Include="..\..\src\Mithril.Shared\Mithril.Shared.csproj" />
  </ItemGroup>
</Project>
```

Reference `Mithril.Shared` for `CdnVersionDetector`. Reference
`Mithril.Reference` for `ParserRegistry` and `IParserSpec`.

Add to [`Mithril.slnx`](../../Mithril.slnx) under the `<Folder Name="/tools/">`
block.

### 2. `Program.cs` — main flow

Roughly ~120 LOC. Sketch:

```csharp
using System.Net.Http;
using Mithril.Reference;
using Mithril.Shared.Reference;

const string CdnRoot = "https://cdn.projectgorgon.com/";
const string FallbackVersion = "v469"; // matches ReferenceDataService.FallbackCdnVersion

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mithril.RefreshAndValidate/1.0");

var version = await CdnVersionDetector.TryDetectAsync(http, CdnRoot)
              ?? FallbackVersion;
Console.WriteLine($"CDN version: {version}");

var tempDir = Directory.CreateTempSubdirectory("mithril-cdn-drift-").FullName;
try
{
    var specs = ParserRegistry.Discover();

    // Fetch in parallel (max 4 concurrent — CDN is small but be polite).
    var fetched = await FetchAllAsync(http, version, specs, tempDir);

    // Validate sequentially so the output order is stable.
    var failures = new List<string>();
    foreach (var spec in specs)
    {
        var json = await File.ReadAllTextAsync(fetched[spec.FileName]);
        try
        {
            var parsed = spec.Parse(json);
            var count = spec.CountEntries(parsed);
            var unknowns = spec.EnumerateUnknowns(parsed).Take(20).ToList();

            var status = unknowns.Count == 0 && count >= spec.MinimumEntryCount
                ? "OK"
                : "FAIL";
            Console.WriteLine($"{status,-4} {spec.FileName,-40} {count,8} entries  {unknowns.Count} unknowns");

            if (status == "FAIL")
            {
                failures.Add(spec.FileName);
                if (count < spec.MinimumEntryCount)
                    Console.WriteLine($"     count {count} < minimum {spec.MinimumEntryCount}");
                foreach (var u in unknowns)
                    Console.WriteLine($"     {u}");
            }
        }
        catch (Exception ex)
        {
            failures.Add(spec.FileName);
            Console.WriteLine($"FAIL {spec.FileName,-40} parse threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    Console.WriteLine();
    if (failures.Count == 0)
    {
        Console.WriteLine($"All {specs.Count} files validated against CDN {version}.");
        return 0;
    }
    Console.Error.WriteLine($"FAIL: {failures.Count}/{specs.Count} files failed validation against CDN {version}.");
    Console.Error.WriteLine("Files: " + string.Join(", ", failures));
    return 1;
}
finally
{
    try { Directory.Delete(tempDir, recursive: true); } catch { }
}
```

`FetchAllAsync` is a small helper using `Task.WhenAll` over a
`SemaphoreSlim`-throttled set of `HttpClient.GetAsync` calls. Keep
concurrency at 4. Each file goes to `Path.Combine(tempDir, spec.FileName)`.

### 3. `IParserSpec` is already public

`Mithril.Reference.IParserSpec`,
`Mithril.Reference.ParserRegistry.Discover`, and
`Mithril.Reference.UnknownReport` are all `public` —
[`IParserSpec.cs`](../../src/Mithril.Reference/IParserSpec.cs) (commit
`aaa36e2`) made the interface public for reflection-based discovery from
the test project. The tool reuses the same surface; no API changes
needed.

### 4. CI workflow

Create `.github/workflows/cdn-drift-check.yml`:

```yaml
name: CDN drift check

on:
  schedule:
    - cron: '0 8 * * *' # 08:00 UTC daily
  workflow_dispatch:

jobs:
  refresh-and-validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet run --project tools/RefreshAndValidate --configuration Release
```

Failure on drift means the workflow goes red and the team gets the
GitHub Actions notification. No extra alerting required for v1.

### 5. Tests for the tool itself

Add `tests/RefreshAndValidate.Tests/` with two basic checks:

1. **`Tool_validates_bundled_data_directly()`** — point the tool at the
   bundled JSON (skip CDN fetch by injecting a fake fetcher) and assert
   exit code 0. Confirms the tool's wiring is correct independent of
   network.
2. **`Tool_returns_nonzero_when_unknowns_present()`** — feed a temp
   directory with one synthetic file that triggers an unknown
   discriminator (same trick as
   [`Unknown_quest_requirement_T_value_emits_diagnostics_warning`](../../tests/Mithril.Shared.Tests/ReferenceDataServiceTests.cs)),
   assert exit code 1.

Refactor `Program.cs` slightly so the core flow is testable as a static
method taking a fetcher delegate; the `Main` thin-wraps it. ~10 LOC of
restructure.

### 6. Update the design notebook

Add a short section to
[docs/mithril-reference-shape-quirks.md](../mithril-reference-shape-quirks.md)
under a new "Operational notes" header (or similar):

> ### Pre-merge drift detection via `tools/RefreshAndValidate`
>
> Daily CI cron fetches every BundledData file from the live CDN and
> runs every `IParserSpec` over them. A red workflow means Elder Game
> shipped a new discriminator value the model layer hasn't been updated
> to recognise. To resolve: identify the new T-value from the workflow
> log, add a concrete subclass + discriminator-map entry, run
> `BundledDataValidationTests` locally, push.

This keeps the doc honest about what *automated* drift detection looks
like vs what the runtime `IDiagnosticsSink` warning does.

## Effort estimate

- Project skeleton + Program.cs: ~3 hours.
- Tests: ~1 hour.
- CI workflow: ~30 min.
- Doc update: 15 min.
- Buffer for CDN flake handling, retry policy, debugging the actions
  permissions: ~1 hour.

**Total: ~½ to 1 day.**

## Edge cases to handle

- **CDN serves an HTML page instead of JSON.** Happens when the version
  detection fails and the tool tries `/v469/data/quests.json` but the
  request 404s into a default page. Detect by content-type
  (`application/json`) or by attempting parse and treating the
  `JsonReaderException` as a fetch-side failure, not a model-side one.
- **CDN version skew between files.** Unlikely but possible —
  `CdnVersionDetector` resolves once per run. If Elder Game pushes a
  partial update mid-run, files might disagree on shape. Accept this:
  the next run will catch it; one-shot failure is fine.
- **Network flake.** Retry each fetch once with a 5-second delay before
  failing the whole run.
- **Concurrent invocations.** Tool always uses a fresh
  `Directory.CreateTempSubdirectory(...)` — no shared state, safe.
- **CI runner with no internet.** `HttpClient.GetAsync` will throw; the
  tool exits 1 with a clear "CDN unreachable" message. CI flake, not a
  drift signal — the workflow can be retried.

## Acceptance criteria

- `dotnet run --project tools/RefreshAndValidate` against the live CDN
  prints a per-file summary and exits 0 today (because Phases 0-6 left
  zero unknowns against the bundled data, and the live CDN should match
  it as of the last bundled refresh).
- Faking an unknown into the temp dir (e.g. by editing the fetched JSON
  before validation) makes the tool exit 1 with a clear error message
  pointing at the discriminator value.
- The CI workflow runs successfully on a manual dispatch.
- New tests pass; existing 1003-test suite still green.

## Out of scope (future follow-ups, in priority order)

1. **Auto-bundled-data-refresh.** When the tool succeeds, copy the temp
   files into `src/Mithril.Shared/Reference/BundledData/` and open a PR.
   Needs reviewer policy; defer.
2. **Field-coverage walker.** Walk raw `JObject`s during validation,
   flag any property name the JSON has that the POCO doesn't. Catches
   "new field added to existing type" — drift the discriminator-only
   check misses.
3. **Slack/email alerting.** Today's "red workflow" is enough. Add real
   alerting only if the team finds GitHub Actions notifications too
   noisy.
4. **Historical tracking.** Log per-run summary to a JSON file in a
   GitHub Actions artifact so drift over time can be visualized. Pure
   nice-to-have.

## Pointers for the implementing agent

- Branch: create from `main` after the `feat/mithril-reference-pocos`
  PR is merged; this work depends on Phases 0-6.
- Per
  [user identity / commit email memory](../../../../.claude/projects/i--src-project-gorgon/memory/user_identity.md):
  commit as `Arthur Conde <arthur.conde@live.com>`.
- Per
  [branch policy memory](../../../../.claude/projects/i--src-project-gorgon/memory/branch_policy_no_direct_commits.md):
  feature branch + `gh pr create`, never push to `main`.
- Per
  [Mithril.Reference design notebook memory](../../../../.claude/projects/i--src-project-gorgon/memory/mithril_reference_design_notebook.md):
  any non-obvious modelling/design choice goes in
  [docs/mithril-reference-shape-quirks.md](../mithril-reference-shape-quirks.md).
- Trust the existing test harness: run
  `dotnet test tests/Mithril.Reference.Tests/Mithril.Reference.Tests.csproj`
  for the bundled-data baseline before/after to confirm no regression.
