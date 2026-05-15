using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using LorebookPoco = Mithril.Reference.Models.Misc.Lorebook;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lorebook detail-pane view-model. Sections top-down:
/// <list type="number">
/// <item><b>Header</b> — Title (large), category subtitle (<c>"from The Gods"</c>),
/// internal-name footer (<c>Book_101 / TheWastedWishes</c>, per the detail-view footer
/// convention).</item>
/// <item><b>Metadata strip</b> — <c>LocationHint</c> (italic flavor) and a folded-in
/// <c>Found in: [Area chip]</c> row. <see cref="LorebookPoco.IsClientLocal"/> is hidden
/// (universal-default noise — true on every bundled entry); <see cref="LorebookPoco.Visibility"/>
/// is hidden by default (found-state mechanic irrelevant to the reference page — cookbook
/// *Default-value noise filtering*; revisit if the smoke-walk says otherwise).</item>
/// <item><b>Body</b> — <see cref="LorebookPoco.Text"/> through the extended
/// <c>FormattedText</c> renderer (#247 Option A: <c>&lt;h1&gt;</c>/<c>&lt;hr&gt;</c>/
/// <c>&lt;br&gt;</c>). 12 of 64 books have null Text (GuideProgram entries point at
/// external volunteer guides) → an italic placeholder.</item>
/// <item><b>Items that bestow this book</b> — a 1:N reverse-lookup rendered as the #318
/// <see cref="ProvenancePopupViewModel"/> popup-from-index. See <see cref="BestowingItemsPopup"/>.</item>
/// </list>
/// <para>
/// <b>#318 — bestowing-items is a popup-from-index, single flat section.</b> The set is
/// materialized exactly once in <see cref="IReferenceDataService.ItemsBestowingLorebook"/>
/// (rebuilt on items.json / lorebooks.json); the popup is a view over that object. This is
/// a <i>single-reason</i> relationship — an item qualifies exactly one way (its
/// <c>BestowLoreBook</c> id == this book's id) — so the popup is passed a <b>single flat
/// section, no provenance sub-sectioning</b> (a lone trivial reason is noise; #318
/// Discipline). There is no synthetic <c>EntityKind</c>, no cap/overflow chip, no Items-tab
/// deep-link. Opening the popup pushes no navigator history (it's a
/// <c>Window.Show()</c> — same non-navigating contract as
/// <c>IReferenceKindTarget.TryOpenInWindow</c>, #229). <see cref="ProvenancePopupViewModel.ToQueryCommand"/>
/// is left null for this surface (consistent with the slice-2 effect→abilities deferral).
/// </para>
/// </summary>
public sealed class LorebookDetailViewModel
{
    /// <summary>
    /// Host-supplied opener for the bestowing-items provenance popup. Defaults to
    /// <see cref="ShowProvenancePopupWindow"/> (creates + <c>Show()</c>s a
    /// <see cref="ProvenancePopupWindow"/>). Tests swap in a capturing delegate so the VM is
    /// fully assertable without spawning a window. Opening the popup this way never calls
    /// <c>IReferenceNavigator</c>, so it pushes no back/forward history.
    /// </summary>
    public static Action<ProvenancePopupViewModel, ICommand?> ProvenancePopupOpener { get; set; }
        = ShowProvenancePopupWindow;

    public LorebookDetailViewModel(
        LorebookListRow row,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        Silmarillion.SilmarillionSettings settings,
        ICommand? openEntityCommand = null)
    {
        Row = row;
        var book = row.Book;
        DisplayName = row.Title;
        InternalName = row.InternalName;
        EnvelopeKey = ResolveEnvelopeKey(refData, book, row.InternalName);
        CategorySubtitle = string.IsNullOrEmpty(row.CategoryDisplayTitle)
            ? null
            : $"from {row.CategoryDisplayTitle}";

        LocationHint = row.LocationHint;

        AreaChip = BuildAreaChip(row.AreaKey, refData, nameResolver, navigator);

        // Null Text → GuideProgram entries point at an external volunteer guide.
        BodyText = string.IsNullOrEmpty(book.Text) ? null : book.Text;
        HasBody = BodyText is not null;

        var (popup, total) = BuildBestowingItemsPopup(row.InternalName, refData, nameResolver, navigator);
        BestowingItemsPopup = popup;
        BestowingItemsTotal = total;

        OpenEntityCommand = openEntityCommand;
        ShowBestowingItemsPopupCommand = new RelayCommand(
            () => ProvenancePopupOpener(BestowingItemsPopup!, OpenEntityCommand),
            () => BestowingItemsPopup is not null);
    }

    private static void ShowProvenancePopupWindow(ProvenancePopupViewModel vm, ICommand? chipClick) =>
        new ProvenancePopupWindow { DataContext = vm, ChipClickCommand = chipClick }.Show();

    public LorebookListRow Row { get; }
    public string DisplayName { get; }

    /// <summary>Bare PascalCase InternalName (e.g. <c>"TheWastedWishes"</c>).</summary>
    public string InternalName { get; }

    /// <summary>
    /// Envelope key (e.g. <c>"Book_101"</c>). Rendered in the footer as
    /// <c>Book_101 / TheWastedWishes</c> — both identifiers, since they diverge.
    /// </summary>
    public string EnvelopeKey { get; }

    /// <summary>Footer text: <c>"&lt;EnvelopeKey&gt; / &lt;InternalName&gt;"</c>.</summary>
    public string FooterText =>
        string.Equals(EnvelopeKey, InternalName, StringComparison.Ordinal)
            ? InternalName
            : $"{EnvelopeKey} / {InternalName}";

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

    /// <summary><c>"from &lt;category display title&gt;"</c> or null when uncategorized.</summary>
    public string? CategorySubtitle { get; }

    public string? LocationHint { get; }
    public bool HasLocationHint => !string.IsNullOrEmpty(LocationHint);

    /// <summary>Single area chip folded into the metadata strip (<c>Found in: [chip]</c>).</summary>
    public EntityChipVm? AreaChip { get; }
    public bool HasArea => AreaChip is not null;

    /// <summary>Body prose, or null for the 12 external-guide books (placeholder shown instead).</summary>
    public string? BodyText { get; }
    public bool HasBody { get; }

    /// <summary>
    /// Distinct count of items that bestow this book (==
    /// <see cref="IReferenceDataService.ItemsBestowingLorebook"/><c>[InternalName].Count</c>,
    /// the index is already dedup'd by item). Drives the <c>"Bestowed by {N} item(s)"</c>
    /// affordance label. 0 ⇒ no affordance rendered.
    /// </summary>
    public int BestowingItemsTotal { get; }

    public bool HasBestowingItems => BestowingItemsPopup is not null;

    /// <summary>
    /// The #318 provenance popup VM for "Items that bestow this book", or null when no item
    /// bestows it. A <b>single flat section</b> fed
    /// <see cref="IReferenceDataService.ItemsBestowingLorebook"/> directly (single-reason —
    /// no provenance sub-sectioning). No second query derivation, so the displayed set
    /// cannot diverge from the index.
    /// </summary>
    public ProvenancePopupViewModel? BestowingItemsPopup { get; }

    /// <summary>
    /// Opens <see cref="BestowingItemsPopup"/> via <see cref="ProvenancePopupOpener"/>.
    /// Bound to the <c>"Bestowed by N item(s)"</c> affordance. Opening pushes no navigator
    /// history (#229 contract).
    /// </summary>
    public ICommand ShowBestowingItemsPopupCommand { get; }

    public ICommand? OpenEntityCommand { get; }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reverse-resolves the <c>"Book_N"</c> envelope key for <paramref name="book"/>. The
    /// POCO doesn't carry it; scan <see cref="IReferenceDataService.Lorebooks"/> for the
    /// entry whose value is this instance (small corpus — 64 entries). Falls back to the
    /// InternalName if not found (defensive — should always resolve).
    /// </summary>
    private static string ResolveEnvelopeKey(
        IReferenceDataService refData, LorebookPoco book, string internalName)
    {
        foreach (var (key, value) in refData.Lorebooks)
        {
            if (ReferenceEquals(value, book)) return key;
        }
        // Fall back to a same-InternalName match (handles a refreshed snapshot where the
        // row's POCO instance predates the swap).
        foreach (var (key, value) in refData.Lorebooks)
        {
            if (string.Equals(value.InternalName, internalName, StringComparison.Ordinal))
                return key;
        }
        return internalName;
    }

    private static EntityChipVm? BuildAreaChip(
        string? areaKey,
        IReferenceDataService refData,
        IEntityNameResolver nameResolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(areaKey)) return null;
        var reference = EntityRef.Area(areaKey);
        var displayName = refData.Areas.TryGetValue(areaKey, out var area)
            ? area.FriendlyName
            : nameResolver.Resolve(reference);
        return new EntityChipVm(
            DisplayName: displayName,
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    /// <summary>
    /// Build the #318 popup-from-index for "Items that bestow this book". Reads
    /// <see cref="IReferenceDataService.ItemsBestowingLorebook"/><c>[internalName]</c>
    /// directly — the single materialization (rebuilt on items.json / lorebooks.json). This
    /// is a single-reason relationship, so the popup gets <b>one flat section</b> (no
    /// provenance sub-sectioning — <see cref="ProvenancePopupViewModel.IsFlat"/> collapses
    /// the section chrome). The count is the index's distinct membership; the popup renders
    /// exactly those members — never a re-derived query result. Returns
    /// <c>(null, 0)</c> when nothing bestows the book (no affordance).
    /// </summary>
    private static (ProvenancePopupViewModel? Popup, int Total) BuildBestowingItemsPopup(
        string internalName,
        IReferenceDataService refData,
        IEntityNameResolver nameResolver,
        IReferenceNavigator navigator)
    {
        if (!refData.ItemsBestowingLorebook.TryGetValue(internalName, out var items)
            || items.Count == 0)
        {
            return (null, 0);
        }

        var chips = items
            .Select(item =>
            {
                var reference = EntityRef.Item(item.InternalName ?? "");
                return new EntityChipVm(
                    DisplayName: nameResolver.Resolve(reference),
                    IconId: item.IconId,
                    Reference: reference,
                    IsNavigable: navigator.CanOpen(reference));
            })
            .ToList();

        // Single flat section — one trivial reason is noise (#318 Discipline). The popup's
        // IsFlat path renders these chips with no section header. ToQueryCommand left null
        // (consistent with the slice-2 effect→abilities deferral) — there is no query
        // derivation for this surface by design.
        var section = new ProvenancePopupSection(
            Label: "Bestowed by",
            Chips: chips);
        var popup = new ProvenancePopupViewModel(
            title: $"Items that bestow {internalName}",
            sections: new[] { section });

        return (popup, chips.Count);
    }
}
