# Releasing Mithril

How to cut a new compiled release. The pipeline is tag-driven: pushing a `v*` tag triggers [`.github/workflows/release.yml`](../.github/workflows/release.yml), which builds two Velopack-packaged SKUs and attaches them to a new GitHub Release.

## What ships per release

Every tag produces both runtime SKUs in parallel, each with a `Setup.exe`, a `Portable.zip`, full and delta `.nupkg`, and a `releases.{channel}.json` manifest:

| Channel | RID | Bundles runtime? | Download size | Notes |
|---|---|---|---|---|
| `selfcontained` | `win-x64` | Yes — .NET 10 Desktop Runtime included | ~270 MB Setup.exe | No prerequisites on user's machine |
| `fxdep` | `win-x64` | No — relies on host's .NET 10 Desktop Runtime | ~40 MB Setup.exe | Friendly dialog points users to the .NET download page if missing |

Both flavors install to `%LocalAppData%\MithrilShell\` (separate from user data at `%LocalAppData%\Mithril\`) and update through Velopack against this repo's GitHub Releases.

## Cutting a release

### 1. Pick a version

Use a SemVer tag in the form `vMAJOR.MINOR.PATCH`. The CI workflow validates this regex and rejects malformed tags. Pre-release suffixes are allowed (`v1.2.3-rc1`).

Look at [previous tags](https://github.com/arthur-conde/project-gorgon/tags) and bump per [SemVer](https://semver.org):

- **Patch** for bug fixes only.
- **Minor** for new modules, new features, or backward-compatible changes.
- **Major** for breaking changes to settings layout or per-character storage that require user intervention.

### 2. Make sure `main` is green

```bash
dotnet build Mithril.slnx -c Release
dotnet test Mithril.slnx -c Release
```

Three filesystem-flake tests in `Gandalf.Tests` and `Mithril.Shared.Tests.CommunityCalibrationServiceTests.RefreshAsync_FetchesAndCaches` are known flaky on `%TEMP%`. Re-run them individually if they fail; the CI workflow does not currently retry, so a flake will fail the release run and require re-tagging.

### 3. Tag the commit

```bash
git checkout main
git pull
git tag v1.2.3
git push origin v1.2.3
```

The tag must be on a commit reachable from `main` so `Nerdbank.GitVersioning` produces a clean public version (the regex `^refs/tags/v\d+(?:\.\d+){1,3}$` in [`version.json`](../version.json) marks tag pushes as public releases).

### 4. Watch the workflow

Open [Actions → Release](https://github.com/arthur-conde/project-gorgon/actions/workflows/release.yml). The job runs on `windows-latest` and takes ~10–15 minutes. Steps:

1. Restore + solution-wide build (so all 10 module DLLs exist before publish).
2. Run the test suite.
3. `dotnet publish` for `selfcontained`, then `vpk pack --channel selfcontained`.
4. `dotnet publish` for `fxdep`, then `vpk pack --channel fxdep`.
5. Upload `releases/*` to a new GitHub Release named `Mithril X.Y.Z` with auto-generated release notes.

The two `--channel` flags are critical: `releases.selfcontained.json` and `releases.fxdep.json` are independent manifests, so a self-contained user never accidentally pulls a framework-dependent update or vice versa.

### 5. Verify the release

After the workflow finishes:

1. Open the [Releases page](https://github.com/arthur-conde/project-gorgon/releases/latest) and confirm both SKUs' `Setup.exe`, `Portable.zip`, `*-full.nupkg`, and `releases.*.json` files are attached.
2. Download `mithril-X.Y.Z-fxdep-Setup.exe` on a test machine and run it. Velopack installs to `%LocalAppData%\MithrilShell\`, creates a Start Menu shortcut, and launches Mithril.
3. In Mithril, open **Settings → About**. Verify:
   - "Channel" reads "Framework-dependent"
   - "Version" matches the tag
   - Status reads "Up to date" (because we just installed the latest)

### 6. Re-tagging (if something goes wrong)

If the workflow fails midway, **delete the tag and re-push**, do not patch over it — partial release artifacts on GitHub will confuse Velopack clients:

```bash
git tag -d v1.2.3
git push --delete origin v1.2.3
# Delete the partial GitHub Release through the web UI
# Fix the issue, then re-tag from the same commit
git tag v1.2.3 <sha>
git push origin v1.2.3
```

## Testing release plumbing without burning a real version

Two safe approaches:

**Sacrificial pre-release tag.** Tag a commit on `main` as `v0.0.1-test1`. The workflow runs the full pipeline and produces installable artifacts you can validate end-to-end. Delete the tag and the GitHub Release after.

**Local Velopack source.** For pure-update-flow testing without GitHub:

```pwsh
# Build version A
dotnet publish src/Mithril.Shell -c Release -r win-x64 --self-contained false `
  -p:MithrilUpdateChannel=fxdep -p:Version=0.0.1 -o publish/0.0.1
vpk pack --packId MithrilShell --packTitle "Project Gorgon" --packAuthors "Arthur Conde" `
  --packVersion 0.0.1 --packDir publish/0.0.1 --mainExe Mithril.exe `
  --channel fxdep --outputDir C:\velo-test\releases

# Install it from the Setup.exe in C:\velo-test\releases\
# Build version B
dotnet publish src/Mithril.Shell -c Release -r win-x64 --self-contained false `
  -p:MithrilUpdateChannel=fxdep -p:Version=0.0.2 -o publish/0.0.2
vpk pack --packId MithrilShell --packTitle "Project Gorgon" --packAuthors "Arthur Conde" `
  --packVersion 0.0.2 --packDir publish/0.0.2 --mainExe Mithril.exe `
  --channel fxdep --outputDir C:\velo-test\releases
```

Then point a debug build at the local source by temporarily swapping `GithubSource` for `SimpleFileSource` in [`MithrilUpdateManager.cs`](../src/Mithril.Shell/Updates/MithrilUpdateManager.cs). Don't commit the swap.

## Local manual publish (no CI)

If you need to produce a build outside CI — e.g., for a one-off internal share — replicate the workflow steps locally:

```pwsh
# Tools (one-time)
dotnet tool install -g vpk

$ver = "1.2.3"

dotnet build Mithril.slnx -c Release -p:Version=$ver

# Self-contained
dotnet publish src/Mithril.Shell -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -p:MithrilUpdateChannel=selfcontained -p:Version=$ver `
  -o publish/selfcontained
vpk pack --packId MithrilShell --packTitle "Project Gorgon" --packAuthors "Arthur Conde" `
  --packVersion $ver --packDir publish/selfcontained --mainExe Mithril.exe `
  --channel selfcontained --icon src/Mithril.Shell/Resources/mithril.ico --outputDir releases

# Framework-dependent
dotnet publish src/Mithril.Shell -c Release -r win-x64 --self-contained false `
  -p:PublishReadyToRun=true -p:MithrilUpdateChannel=fxdep -p:Version=$ver `
  -o publish/fxdep
vpk pack --packId MithrilShell --packTitle "Project Gorgon" --packAuthors "Arthur Conde" `
  --packVersion $ver --packDir publish/fxdep --mainExe Mithril.exe `
  --channel fxdep --icon src/Mithril.Shell/Resources/mithril.ico --outputDir releases
```

The `releases/` folder is gitignored. Both `publish/` and `releases/` regenerate cleanly; you can delete them between runs.

## Things that are not (yet) automated

- **Code signing.** Setup.exe ships unsigned, so the first ~50 downloads will SmartScreen-warn end users. Mention this in release notes until we have a cert.
- **ARM64 builds.** Win-x64 only.
- **Release notes curation.** GitHub's auto-generated notes ship as-is. Edit the Release through the web UI after it lands if a particular change deserves emphasis.
- **Post-release smoke check.** Manual — see step 5.

## File reference

| File | Role |
|---|---|
| [`.github/workflows/release.yml`](../.github/workflows/release.yml) | The pipeline triggered by `v*` tag pushes |
| [`version.json`](../version.json) | `Nerdbank.GitVersioning` config; `publicReleaseRefSpec` includes the tag pattern |
| [`Directory.Packages.props`](../Directory.Packages.props) | Velopack version pin |
| [`src/Mithril.Shell/Mithril.Shell.csproj`](../src/Mithril.Shell/Mithril.Shell.csproj) | `MithrilUpdateChannel` MSBuild property + post-publish module-staging target |
| [`src/Mithril.Shell/Updates/`](../src/Mithril.Shell/Updates/) | Velopack checker, applier, channel-marker reader |
