using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Attached behavior that renders the <b>Structure</b> tier's ratified G4 typographic
/// treatment — <c>text-transform: uppercase</c> + <c>letter-spacing ≈ 0.08em</c> — onto a
/// <see cref="TextBlock"/>. G3's grammar fixed the Structure tier as "Tracked uppercase
/// (letter-spacing 0.08em). 9–9.5pt. Weight 600"; the repo-side note in
/// <c>docs/silmarillion-visual-grammar.md</c> deferred the *uppercasing + tracking* because
/// WPF's <see cref="TextBlock"/> has <b>no native <c>letter-spacing</c></b>. G4 ratifies
/// applying the treatment now; this behavior is that encoding.
/// <para>
/// <b>Why an attached behavior, applied via Style.</b> The three Structure styles
/// (<c>StructureSectionLabelStyle</c>, <c>StructureInlinePrefixStyle</c>,
/// <c>StructureGroupHeaderStyle</c>) are consumed by ~9 fan-out views whose TextBlocks set
/// <see cref="TextBlock.Text"/> directly (literal or bound). A Style setter switches
/// <see cref="IsEnabledProperty"/> on; the behavior then reads the block's own
/// <see cref="TextBlock.Text"/>, rebuilds <see cref="TextBlock.Inlines"/> as the tracked
/// upper-cased form, and keeps it in sync — so no call site changes (the Recipe pilot keeps
/// working unchanged). Mirrors the <see cref="FormattedText"/> attached-property pattern.
/// </para>
/// <para>
/// <b>Tracking technique (WPF has no letter-spacing).</b> The content is rebuilt as one
/// <see cref="Run"/> per upper-cased character, interleaved with a thin spacer
/// <see cref="Run"/> whose text is a single Unicode <c>HAIR SPACE</c> (<c>U+200A</c>). A
/// hair space's intrinsic advance width is itself defined relative to the font em
/// (~0.06–0.1em in the body fonts Mithril ships), so it scales automatically with the
/// inherited <see cref="Control.FontSize"/> — i.e. it stays em-correct as the Appearance
/// base-size slider moves, with <b>no</b> width binding or converter needed (consistent with
/// the grammar's "all sizes are em-relative" rule). <b>Fidelity caveat:</b> this is a
/// glyph-spacer approximation of true kerning-level tracking — it targets ≈0.08em but the
/// exact advance is the font's hair-space metric, not a pixel-exact 0.08em. That is the
/// expected, accepted trade-off for short uppercase labels (per the G4 dispatch); do not
/// read it as pixel-exact tracking.
/// </para>
/// <para>
/// <b>Punctuation.</b> A trailing <c>:</c> (the <c>StructureInlinePrefixStyle</c> idiom —
/// <c>Field:</c>) is preserved literally: <see cref="char.ToUpperInvariant(char)"/> is a
/// no-op on punctuation, and the terminating <c>:</c> still gets a *leading* hair-space so
/// it tracks consistently with the letters before it (it reads as part of the same tracked
/// label, not a tight-set afterthought). Empty / null / single-char text is safe (no spacer
/// for &lt; 2 visible characters).
/// </para>
/// <para>
/// Usage (applied through the Structure styles; not authored per call site):
/// <code>
/// &lt;Setter Property="c:StructureLabel.IsEnabled" Value="True"/&gt;
/// </code>
/// </para>
/// </summary>
public static class StructureLabel
{
    /// <summary>
    /// Unicode <c>HAIR SPACE</c> (<c>U+200A</c>) — the inter-character spacer glyph. Chosen
    /// because its advance is font-em-relative, so the rendered tracking scales with the
    /// inherited <see cref="Control.FontSize"/> without an explicit width binding.
    /// </summary>
    public const char HairSpace = ' ';

    /// <summary>
    /// Switches the tracked-uppercase Structure treatment on for the attached
    /// <see cref="TextBlock"/>. Set <c>True</c> by the Structure styles; the behavior then
    /// owns <see cref="TextBlock.Inlines"/> for that block.
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(StructureLabel),
            new PropertyMetadata(defaultValue: false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        // Detach first so a style re-application / toggle can't double-subscribe.
        textBlock.Loaded -= OnTextBlockLoaded;
        DependencyPropertyDescriptor
            .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .RemoveValueChanged(textBlock, OnTrackedSourceChanged);
        DependencyPropertyDescriptor
            .FromProperty(TextBlock.FontSizeProperty, typeof(TextBlock))
            .RemoveValueChanged(textBlock, OnTrackedSourceChanged);

        if (e.NewValue is not true) return;

        // Re-render on Text change AND on (inherited) FontSize change so the hair-space
        // tracking stays em-correct when the Appearance slider moves the inherited size —
        // mirrors how the rest of the grammar binds live to FontSize.
        DependencyPropertyDescriptor
            .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(textBlock, OnTrackedSourceChanged);
        DependencyPropertyDescriptor
            .FromProperty(TextBlock.FontSizeProperty, typeof(TextBlock))
            .AddValueChanged(textBlock, OnTrackedSourceChanged);

        // The em-source FontSize binding (FindAncestor in the Style) often resolves only
        // once the element is in the visual tree, so render on Loaded too.
        textBlock.Loaded += OnTextBlockLoaded;
        Render(textBlock);
    }

    private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb) Render(tb);
    }

    private static void OnTrackedSourceChanged(object? sender, System.EventArgs e)
    {
        if (sender is TextBlock tb) Render(tb);
    }

    private static bool _rendering;

    private static void Render(TextBlock textBlock)
    {
        // Reading Text after we've replaced Inlines yields the concatenated run text
        // (which already includes hair-spaces) — guard against the resulting feedback loop.
        if (_rendering) return;
        _rendering = true;
        try
        {
            var source = textBlock.Text ?? string.Empty;

            // The concatenation of our own emitted runs (letters + hair-spaces). If Text
            // already equals that, the visible label is unchanged — re-rendering would
            // just re-tokenise our own output. Skip.
            if (source.IndexOf(HairSpace) >= 0) return;

            var inlines = BuildTrackedInlines(source);
            textBlock.Inlines.Clear();
            foreach (var inline in inlines)
                textBlock.Inlines.Add(inline);
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>
    /// Pure transform: upper-cases <paramref name="text"/> (invariant culture) and
    /// interleaves a <see cref="HairSpace"/> spacer <see cref="Run"/> between every adjacent
    /// pair of characters, producing the tracked-uppercase <see cref="Inline"/> sequence the
    /// behavior assigns to <see cref="TextBlock.Inlines"/>. Factored out (no visual tree) so
    /// the upper-casing + spacing contract is unit-testable, exactly like
    /// <see cref="FormattedText.BuildInlines"/>.
    /// <para>
    /// The spacer is em-correct by construction: a hair space's advance is defined relative
    /// to the font em, so it scales with whatever <see cref="Control.FontSize"/> the runs
    /// inherit — no per-call width math. Returns an empty list for null/empty input and a
    /// single <see cref="Run"/> (no spacer) for a single-character label.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Inline> BuildTrackedInlines(string? text)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(text)) return result;

        var upper = text!.ToUpperInvariant();
        for (var i = 0; i < upper.Length; i++)
        {
            if (i > 0)
                result.Add(new Run(HairSpace.ToString()));
            result.Add(new Run(upper[i].ToString(CultureInfo.InvariantCulture)));
        }

        return result;
    }

    /// <summary>
    /// Pure <c>string → string</c> helper: the rendered text the tracked label produces
    /// (upper-cased, hair-space between every adjacent character). Equivalent to
    /// concatenating <see cref="BuildTrackedInlines"/>'s run text; provided for tests that
    /// want to assert the transform without enumerating inlines.
    /// </summary>
    public static string TrackedText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var upper = text!.ToUpperInvariant();
        if (upper.Length < 2) return upper;
        return string.Join(HairSpace.ToString(), upper.ToCharArray());
    }
}
