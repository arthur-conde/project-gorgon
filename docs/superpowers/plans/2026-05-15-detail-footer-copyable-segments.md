# Detail-footer copyable segments — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make each identifier in the Lorebook detail footer independently click-to-copy, without changing any other detail view or the exported card image dimensions.

**Architecture:** Add a general `FooterSegments` (`IEnumerable<string>`) dependency property to the shared `DetailExportHost`. When set, the footer template renders the strings as independent click-to-copy chips joined by an inert middot, each with its own ~1.2 s "copied" ack; when unset, the existing single-button `FooterText` path is unchanged. `LorebookDetailViewModel` exposes `FooterSegments` = `[EnvelopeKey, InternalName]` (or one element when they coincide); the Lorebook view binds it instead of `FooterText`.

**Tech Stack:** .NET 10 / WPF, C# (nullable, warnings-as-errors), CommunityToolkit.Mvvm, xunit + FluentAssertions. Spec: `docs/superpowers/specs/2026-05-15-detail-footer-copyable-segments-design.md`.

---

## File Structure

- **Modify** `src/Silmarillion.Module/ViewModels/LorebookDetailViewModel.cs` — add `FooterSegments`; keep `FooterText`.
- **Modify** `tests/Silmarillion.Tests/ViewModels/LorebookDetailViewModelTests.cs` — new `FooterSegments` cases.
- **Modify** `src/Mithril.Shared.Wpf/DetailExportHost.cs` — add `FooterSegments` DP, the `FooterSegmentItem` projection type, `FooterSegmentItems`/`HasFooterSegments` read-only DPs, per-segment copy + ack.
- **Modify** `src/Mithril.Shared.Wpf/Resources.xaml` — `DetailExportHost` template: add the segment `ItemsControl` path; collapse the single-button footer when segments are present.
- **Modify** `src/Silmarillion.Module/Views/LorebookDetailView.xaml` — bind `FooterSegments` instead of `FooterText`.

No other detail view, VM, or test is touched. Build auto-copies module DLLs (`Directory.Build.targets`); close Mithril/VS before building to avoid silent stale-DLL file locks.

---

## Task 1: Lorebook VM — `FooterSegments` (TDD)

**Files:**
- Modify: `src/Silmarillion.Module/ViewModels/LorebookDetailViewModel.cs`
- Test: `tests/Silmarillion.Tests/ViewModels/LorebookDetailViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these two tests to `tests/Silmarillion.Tests/ViewModels/LorebookDetailViewModelTests.cs` (after `Header_ResolvesTitle_CategorySubtitle_AndDivergentFooter`, before `AreaChip_ResolvesFromFirstMatchingKeyword`):

```csharp
    [Fact]
    public void FooterSegments_DivergentKeyAndName_AreTwoIndependentSegments()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("Book_101", new LorebookPoco
        {
            Category = "Stories", InternalName = "TheWastedWishes",
            Title = "The Wasted Wishes", Text = "x",
        });
        var vm = BuildDetail(refData, "TheWastedWishes");

        // Each is its own atomic copyable identifier (no " / " mashup).
        vm.FooterSegments.Should().Equal("Book_101", "TheWastedWishes");
        // Back-compat: joined FooterText preserved for non-UI consumers.
        vm.FooterText.Should().Be("Book_101 / TheWastedWishes");
    }

    [Fact]
    public void FooterSegments_KeyEqualsName_IsSingleSegment()
    {
        var refData = new FakeReferenceData();
        refData.AddLorebook("SameName", new LorebookPoco
        {
            Category = "Stories", InternalName = "SameName",
            Title = "Same", Text = "x",
        });
        var vm = BuildDetail(refData, "SameName");

        vm.FooterSegments.Should().ContainSingle().Which.Should().Be("SameName");
        vm.FooterText.Should().Be("SameName");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~LorebookDetailViewModelTests.FooterSegments"`

Expected: FAIL — compilation error `'LorebookDetailViewModel' does not contain a definition for 'FooterSegments'`.

- [ ] **Step 3: Implement `FooterSegments`**

In `src/Silmarillion.Module/ViewModels/LorebookDetailViewModel.cs`, immediately after the `FooterText` property (the block ending at the line `: $"{EnvelopeKey} / {InternalName}";`), add:

```csharp
    /// <summary>
    /// Footer identifiers as independent copyable segments, bound to
    /// <c>DetailExportHost.FooterSegments</c> (each renders as its own click-to-copy
    /// chip joined by an inert middot). For real lorebooks the envelope key and
    /// InternalName always diverge → two segments <c>[Book_101, TheWastedWishes]</c>;
    /// the defensive equal case collapses to a single segment. This replaces copying
    /// the joined <see cref="FooterText"/> slug, which was never a valid identifier.
    /// </summary>
    public IReadOnlyList<string> FooterSegments =>
        string.Equals(EnvelopeKey, InternalName, StringComparison.Ordinal)
            ? new[] { InternalName }
            : new[] { EnvelopeKey, InternalName };
```

If `System.Collections.Generic` is not already in scope (it is via the global usings for this project, but verify), no `using` change is needed — `IReadOnlyList<>` resolves through the project's implicit usings. Do **not** modify or remove the existing `FooterText` property.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~LorebookDetailViewModelTests.FooterSegments"`
Expected: PASS (2 passed).

- [ ] **Step 5: Run the full Lorebook test class to confirm no regression**

Run: `dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~LorebookDetailViewModelTests"`
Expected: PASS — all existing tests (incl. `Header_ResolvesTitle_CategorySubtitle_AndDivergentFooter` asserting `FooterText == "Book_101 / TheWastedWishes"`) still green.

- [ ] **Step 6: Commit**

```bash
git add src/Silmarillion.Module/ViewModels/LorebookDetailViewModel.cs tests/Silmarillion.Tests/ViewModels/LorebookDetailViewModelTests.cs
git -c commit.gpgsign=false commit -m "feat(silmarillion): Lorebook FooterSegments — split key/name into copyable parts

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `DetailExportHost` — `FooterSegments` DP, projection, per-segment copy + ack

**Files:**
- Modify: `src/Mithril.Shared.Wpf/DetailExportHost.cs`

This is shared WPF control code. It has no existing unit coverage (the current footer copy has none either — it is STA/clipboard UI); verification is the build gate here plus the manual smoke in Task 5, consistent with the spec.

- [ ] **Step 1: Add usings**

At the top of `src/Mithril.Shared.Wpf/DetailExportHost.cs`, the current usings are:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
```

Replace that block with:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
```

- [ ] **Step 2: Add the `FooterSegments` DP and the projected read-only DPs**

In `DetailExportHost`, immediately after the existing `FooterText` property block (the `get`/`set` ending at `set => SetValue(FooterTextProperty, value);` and its closing `}`), add:

```csharp
    public static readonly DependencyProperty FooterSegmentsProperty =
        DependencyProperty.Register(
            nameof(FooterSegments), typeof(IEnumerable<string>),
            typeof(DetailExportHost),
            new PropertyMetadata(null, OnFooterSegmentsChanged));

    /// <summary>
    /// When non-null and non-empty, the footer renders these strings as independent
    /// click-to-copy chips joined by an inert middot, instead of the single
    /// <see cref="FooterText"/> button. Each chip copies exactly its own string —
    /// the separator is never part of any copy payload. Null/empty → the
    /// <see cref="FooterText"/> path is used unchanged.
    /// </summary>
    public IEnumerable<string>? FooterSegments
    {
        get => (IEnumerable<string>?)GetValue(FooterSegmentsProperty);
        set => SetValue(FooterSegmentsProperty, value);
    }

    private static readonly DependencyPropertyKey FooterSegmentItemsKey =
        DependencyProperty.RegisterReadOnly(
            nameof(FooterSegmentItems), typeof(IReadOnlyList<FooterSegmentItem>),
            typeof(DetailExportHost),
            new PropertyMetadata(System.Array.Empty<FooterSegmentItem>()));

    public static readonly DependencyProperty FooterSegmentItemsProperty =
        FooterSegmentItemsKey.DependencyProperty;

    /// <summary>Projected, per-item-ack-bearing view of <see cref="FooterSegments"/>
    /// for the template's <c>ItemsControl</c>. Empty when no segments.</summary>
    public IReadOnlyList<FooterSegmentItem> FooterSegmentItems =>
        (IReadOnlyList<FooterSegmentItem>)GetValue(FooterSegmentItemsProperty);

    private static readonly DependencyPropertyKey HasFooterSegmentsKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasFooterSegments), typeof(bool), typeof(DetailExportHost),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasFooterSegmentsProperty =
        HasFooterSegmentsKey.DependencyProperty;

    /// <summary>True when <see cref="FooterSegments"/> has ≥1 non-empty entry; the
    /// template then shows the segment ItemsControl and hides the single-button
    /// footer.</summary>
    public bool HasFooterSegments => (bool)GetValue(HasFooterSegmentsProperty);

    private static void OnFooterSegmentsChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (DetailExportHost)d;
        var segments = (e.NewValue as IEnumerable<string>)?
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList() ?? new List<string>();

        var items = new List<FooterSegmentItem>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var item = new FooterSegmentItem(segments[i], isFirst: i == 0);
            item.CopyCommand = new RelayCommand(() => host.CopySegment(item));
            items.Add(item);
        }

        host.SetValue(FooterSegmentItemsKey, items);
        host.SetValue(HasFooterSegmentsKey, items.Count > 0);
    }

    private void CopySegment(FooterSegmentItem item)
    {
        try
        {
            Clipboard.SetDataObject(
                new DataObject(DataFormats.UnicodeText, item.Text), copy: true);
        }
        catch
        {
            return; // clipboard can transiently fail; no ack, user can retry
        }

        item.Copied = true;
        var timer = new DispatcherTimer { Interval = AckHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            item.Copied = false;
        };
        timer.Start();
    }
```

- [ ] **Step 3: Add the `FooterSegmentItem` type**

At the end of `src/Mithril.Shared.Wpf/DetailExportHost.cs`, *after* the closing brace of the `DetailExportHost` class but inside the namespace, add:

```csharp
/// <summary>
/// One copyable footer chip. <see cref="Copied"/> drives a transient "copied" ack on
/// just this segment (the other segments are unaffected); <see cref="IsFirst"/>
/// suppresses the leading middot separator for the first chip. Public because the
/// <c>DetailExportHost</c> template binds to these properties (WPF binding requires
/// public members).
/// </summary>
public sealed partial class FooterSegmentItem : ObservableObject
{
    public FooterSegmentItem(string text, bool isFirst)
    {
        Text = text;
        IsFirst = isFirst;
    }

    /// <summary>The atomic identifier shown and copied verbatim.</summary>
    public string Text { get; }

    /// <summary>First chip in the row → no leading separator.</summary>
    public bool IsFirst { get; }

    [ObservableProperty]
    private bool _copied;

    /// <summary>Set by the host immediately after construction; copies
    /// <see cref="Text"/> and triggers this segment's ack.</summary>
    public IRelayCommand CopyCommand { get; set; } = null!;
}
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: Build succeeded, 0 errors (warnings-as-errors is on — must be 0 warnings too). `[ObservableProperty]` on `_copied` generates the public `Copied` property used by the template.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared.Wpf/DetailExportHost.cs
git -c commit.gpgsign=false commit -m "feat(shared-wpf): DetailExportHost FooterSegments — independent copyable footer chips

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `DetailExportHost` template — segment `ItemsControl` path

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Resources.xaml` (the `DetailExportHost` `Style`, currently lines ~935-994)

- [ ] **Step 1: Collapse the single-button footer when segments are present**

In `src/Mithril.Shared.Wpf/Resources.xaml`, find `PART_FooterButton`. Its opening tag currently ends with this inline attribute:

```xml
                                    Visibility="{Binding FooterText, RelativeSource={RelativeSource TemplatedParent}, Converter={StaticResource NullOrEmptyToVis}}">
```

Remove that `Visibility="..."` attribute from the opening tag (so the tag now ends `ToolTip="Click to copy the internal name">`), and add this `Button.Style` as the **first child** of the `<Button x:Name="PART_FooterButton" ...>` element (before `<Button.Template>`):

```xml
                                <Button.Style>
                                    <Style TargetType="Button">
                                        <Setter Property="Visibility"
                                                Value="{Binding FooterText, RelativeSource={RelativeSource TemplatedParent}, Converter={StaticResource NullOrEmptyToVis}}"/>
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding HasFooterSegments, RelativeSource={RelativeSource TemplatedParent}}" Value="True">
                                                <Setter Property="Visibility" Value="Collapsed"/>
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Button.Style>
```

- [ ] **Step 2: Add the segment ItemsControl directly after `PART_FooterButton`**

Immediately after the `</Button>` that closes `PART_FooterButton`, and **before** the `<ContentPresenter/>` line, insert:

```xml
                            <!-- Segment footer (#Lorebook): N independent click-to-copy
                                 chips joined by an inert middot. Shown only when
                                 FooterSegments is set; mutually exclusive with
                                 PART_FooterButton. Single-line → export height
                                 unchanged. -->
                            <ItemsControl x:Name="PART_FooterSegments"
                                          DockPanel.Dock="Bottom"
                                          HorizontalAlignment="Right"
                                          Margin="0,8,14,12"
                                          ItemsSource="{Binding FooterSegmentItems, RelativeSource={RelativeSource TemplatedParent}}"
                                          Visibility="{Binding HasFooterSegments, RelativeSource={RelativeSource TemplatedParent}, Converter={StaticResource BoolToVis}}">
                                <ItemsControl.ItemsPanel>
                                    <ItemsPanelTemplate>
                                        <StackPanel Orientation="Horizontal"/>
                                    </ItemsPanelTemplate>
                                </ItemsControl.ItemsPanel>
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <StackPanel Orientation="Horizontal">
                                            <TextBlock Text="  ·  " Foreground="#55FFFFFF"
                                                       FontFamily="{DynamicResource AppMonoFontFamily}"
                                                       FontSize="{DynamicResource AppFontSizeSmall}"
                                                       VerticalAlignment="Center">
                                                <TextBlock.Style>
                                                    <Style TargetType="TextBlock">
                                                        <Setter Property="Visibility" Value="Visible"/>
                                                        <Style.Triggers>
                                                            <DataTrigger Binding="{Binding IsFirst}" Value="True">
                                                                <Setter Property="Visibility" Value="Collapsed"/>
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </TextBlock.Style>
                                            </TextBlock>
                                            <Button Command="{Binding CopyCommand}" Cursor="Hand"
                                                    Background="Transparent" BorderThickness="0"
                                                    Padding="0" ToolTip="Click to copy">
                                                <Button.Template>
                                                    <ControlTemplate TargetType="Button">
                                                        <ContentPresenter/>
                                                    </ControlTemplate>
                                                </Button.Template>
                                                <TextBlock FontFamily="{DynamicResource AppMonoFontFamily}"
                                                           FontSize="{DynamicResource AppFontSizeSmall}">
                                                    <TextBlock.Style>
                                                        <Style TargetType="TextBlock">
                                                            <Setter Property="Text" Value="{Binding Text}"/>
                                                            <Setter Property="Foreground" Value="#88FFFFFF"/>
                                                            <Style.Triggers>
                                                                <DataTrigger Binding="{Binding Copied}" Value="True">
                                                                    <Setter Property="Text" Value="copied ✓"/>
                                                                    <Setter Property="Foreground" Value="#FFD4A847"/>
                                                                </DataTrigger>
                                                            </Style.Triggers>
                                                        </Style>
                                                    </TextBlock.Style>
                                                </TextBlock>
                                            </Button>
                                        </StackPanel>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
```

- [ ] **Step 3: Build to verify the XAML compiles**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: Build succeeded, 0 errors/0 warnings. (`BoolToVis` and `NullOrEmptyToVis` are already-defined resources in this same `Resources.xaml`; `AppMonoFontFamily`/`AppFontSizeSmall` are existing dynamic resources used by the original footer.)

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.Shared.Wpf/Resources.xaml
git -c commit.gpgsign=false commit -m "feat(shared-wpf): DetailExportHost template renders FooterSegments chips

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Lorebook view binds `FooterSegments`

**Files:**
- Modify: `src/Silmarillion.Module/Views/LorebookDetailView.xaml:23`

- [ ] **Step 1: Switch the binding**

In `src/Silmarillion.Module/Views/LorebookDetailView.xaml`, change line 23 from:

```xml
    <c:DetailExportHost FooterText="{Binding FooterText}">
```

to:

```xml
    <c:DetailExportHost FooterSegments="{Binding FooterSegments}">
```

Leave the comment on line 27 (`<!-- Footer ... is owned by the wrapping DetailExportHost ... -->`) as-is; it still describes ownership correctly.

- [ ] **Step 2: Build the module**

Run: `dotnet build src/Silmarillion.Module/Silmarillion.Module.csproj`
Expected: Build succeeded, 0 errors/0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Silmarillion.Module/Views/LorebookDetailView.xaml
git -c commit.gpgsign=false commit -m "feat(silmarillion): Lorebook detail footer uses copyable segments

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Full verification gate

**Files:** none (verification only)

- [ ] **Step 1: Full solution build**

Run: `dotnet build Mithril.slnx`
Expected: Build succeeded, 0 errors, 0 warnings. (If `MSB3026/MSB3027` file-lock errors appear, Mithril or VS is holding a module DLL — close them and rebuild; a "no effect" rebuild is a deploy failure, not a code issue.)

- [ ] **Step 2: Full test run**

Run: `dotnet test Mithril.slnx`
Expected: All tests pass. Pay attention to `Silmarillion.Tests` — `LorebookDetailViewModelTests` (new `FooterSegments` cases + unchanged `FooterText` cases) and the smoke-walk test asserting `FooterText.Should().Contain("/")` must all be green.

- [ ] **Step 3: Manual smoke (the WPF behavior with no unit coverage)**

Run: `dotnet run --project src/Mithril.Shell`

Verify:
1. Silmarillion → **Lorebooks** tab → select "The Wasted Wishes". Footer reads `Book_101 · TheWastedWishes` (middot separator, single line, bottom-right, mono).
2. Click `Book_101` → it alone flips to `copied ✓` (gold) for ~1.2 s; the other segment is unaffected. Paste elsewhere → clipboard is exactly `Book_101` (no ` / `, no name).
3. Click `TheWastedWishes` → it alone acks; clipboard is exactly `TheWastedWishes`.
4. Switch to a detail view that does **not** set segments (e.g. Abilities tab → any ability). Footer is the single `InternalName` button as before; clicking it still copies and acks (regression check on the unchanged `FooterText` path).
5. On a Lorebook detail, click the top-right camera "Copy as image". Exported card looks the same height as before (footer still one line) and includes the dot-separated footer.

- [ ] **Step 4: Final state check**

Run: `git status --short && git log --oneline -5`
Expected: clean working tree (aside from pre-existing unrelated `scripts/start.ps1` and untracked `docs/agent-plans/*`); the 4 feature commits + the spec commit present on `feat/silmarillion-footer-copyable-segments`.

---

## Self-Review

**Spec coverage:**
- §1 shared control `FooterSegments` + per-item ack + single-segment fallback → Task 2 + Task 3 (collapse trigger). ✓
- §2 Lorebook VM `FooterSegments`, `FooterText` kept → Task 1. ✓
- §3 view binding swap → Task 4. ✓
- §4 data flow (click → copy own string, separator never copied) → Task 2 `CopySegment` + Task 3 inert middot. ✓
- §5 export height unchanged (single line) → Task 3 horizontal layout + Task 5 step 3.5 manual check. ✓
- §6 testing (VM TDD; control verified manually) → Task 1 (TDD) + Task 5 step 3. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code; commands have expected output. ✓

**Type consistency:** `FooterSegments` is `IEnumerable<string>` on the host DP, `IReadOnlyList<string>` on the VM (assignable to the DP) — consistent. `FooterSegmentItem` members (`Text`, `IsFirst`, `Copied`, `CopyCommand`) match exactly between Task 2 (definition) and Task 3 (template bindings). `FooterSegmentItems`/`HasFooterSegments` names match between Task 2 DPs and Task 3 bindings. ✓
