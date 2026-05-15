using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Attached property + parser that renders Project Gorgon's inline-markup vocabulary
/// (<c>&lt;i&gt;…&lt;/i&gt;</c>, <c>&lt;b&gt;…&lt;/b&gt;</c>, <c>&lt;h1&gt;…&lt;/h1&gt;</c>,
/// <c>&lt;hr&gt;</c>, <c>&lt;br&gt;</c>) into a <see cref="TextBlock"/>'s
/// <see cref="TextBlock.Inlines"/> collection. Quest descriptions, lorebook bodies and item
/// flavor text all use this subset — most commonly as a speaker prefix
/// (<c>&lt;i&gt;Joeh:&lt;/i&gt; Hello adventurer.</c>) or, for lorebooks, an opening
/// <c>&lt;h1&gt;</c> title plus the occasional <c>&lt;hr&gt;</c> scene break. A plain
/// <c>TextBlock.Text</c> binding renders the tags literally; this property parses them so the
/// block reads as intended.
/// <para>
/// <b>Tag vocabulary (#247 — Option A).</b> The original two-tag subset (<c>&lt;i&gt;</c> /
/// <c>&lt;b&gt;</c>) was extended with the three structural tags lorebook bodies use:
/// <list type="bullet">
/// <item><c>&lt;br&gt;</c> / <c>&lt;br/&gt;</c> → a single <see cref="LineBreak"/>.</item>
/// <item><c>&lt;hr&gt;</c> / <c>&lt;hr/&gt;</c> → a scene-break: blank line, a row of
/// em-dashes on its own line, blank line. (<c>TextBlock.Inlines</c> has no
/// <c>Border</c>/<c>Separator</c> inline; the em-dash row is the lightweight idiom — see the
/// #247 handoff's Critical → Option A note.)</item>
/// <item><c>&lt;h1&gt;…&lt;/h1&gt;</c> → a leading line break, the heading text rendered
/// <b>bold + larger</b>, then a trailing double line break (it opens the book; the body
/// follows). Bold/italic state from enclosing tags is preserved.</item>
/// </list>
/// Extending the shared renderer (rather than pre-stripping in one VM) is deliberate: the
/// existing call sites (quest descriptions, item flavor text) start parsing these tags too,
/// which is the desired behaviour — those fields previously rendered literal <c>&lt;br&gt;</c>
/// etc. on the rare entry that carried one. No consumer relied on literal pass-through (the
/// only producers of these tags are long-form text fields that <i>want</i> them rendered).
/// </para>
/// <para>
/// The parser handles nesting (<c>&lt;b&gt;&lt;i&gt;X&lt;/i&gt;&lt;/b&gt;</c>) via formatting
/// counts; unbalanced or interleaved tags clamp at zero without throwing, so data drift can't
/// crash the renderer. A bare <c>&lt;</c> not followed by one of the supported tags (including
/// a malformed <c>&lt;h1</c> with no <c>&gt;</c>) passes through as literal text.
/// </para>
/// <para>
/// Usage:
/// <code>
/// xmlns:wpf="clr-namespace:Mithril.Shared.Wpf;assembly=Mithril.Shared.Wpf"
/// &lt;TextBlock wpf:FormattedText.Text="{Binding Description}" TextWrapping="Wrap" /&gt;
/// </code>
/// Setting the attached property replaces <see cref="TextBlock.Inlines"/> wholesale; don't mix
/// with hand-authored <c>&lt;Run/&gt;</c> children on the same block.
/// </para>
/// </summary>
public static class FormattedText
{
    /// <summary>The em-dash separator row emitted for <c>&lt;hr&gt;</c>.</summary>
    private const string HorizontalRuleGlyph = "— — — — — — —";

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(FormattedText),
            new PropertyMetadata(defaultValue: null, OnTextChanged));

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;
        textBlock.Inlines.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;
        foreach (var inline in BuildInlines(text!))
            textBlock.Inlines.Add(inline);
    }

    /// <summary>
    /// Tokenise <paramref name="text"/> and yield the WPF <see cref="Inline"/> runs (and
    /// <see cref="LineBreak"/>s) that would render it with the embedded markup applied. Pure
    /// function — usable from tests without instantiating a TextBlock.
    /// </summary>
    public static IReadOnlyList<Inline> BuildInlines(string text)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(text)) return result;

        // Counts rather than a stack so unbalanced/interleaved data drift can't crash —
        // each </tag> decrements (clamped at 0), each <tag> increments. A run inherits the
        // current italic/bold/heading state at emission time.
        var italicDepth = 0;
        var boldDepth = 0;
        var headingDepth = 0;
        var pos = 0;

        void EmitText(string segment)
        {
            if (segment.Length == 0) return;
            result.Add(MakeRun(segment, italicDepth, boldDepth, headingDepth));
        }

        while (pos < text.Length)
        {
            var tag = FindNextTag(text, pos);

            if (tag.Pos < 0)
            {
                EmitText(text.Substring(pos));
                break;
            }

            if (tag.Pos > pos)
                EmitText(text.Substring(pos, tag.Pos - pos));

            switch (tag.Kind)
            {
                case TagKind.Italic:
                    italicDepth = System.Math.Max(0, italicDepth + tag.Delta);
                    break;
                case TagKind.Bold:
                    boldDepth = System.Math.Max(0, boldDepth + tag.Delta);
                    break;
                case TagKind.Heading:
                    if (tag.Delta > 0)
                    {
                        // Opening <h1>: break onto a fresh line, then bold+large text.
                        result.Add(new LineBreak());
                        headingDepth = headingDepth + 1;
                    }
                    else
                    {
                        // Closing </h1>: end the heading, double-break before the body.
                        headingDepth = System.Math.Max(0, headingDepth - 1);
                        result.Add(new LineBreak());
                        result.Add(new LineBreak());
                    }
                    break;
                case TagKind.LineBreak:
                    result.Add(new LineBreak());
                    break;
                case TagKind.HorizontalRule:
                    // Scene break: blank line, em-dash row, blank line. No inline Border in
                    // TextBlock.Inlines, so the glyph row is the lightweight separator idiom.
                    result.Add(new LineBreak());
                    result.Add(new Run(HorizontalRuleGlyph) { Foreground = SystemColors.GrayTextBrush });
                    result.Add(new LineBreak());
                    result.Add(new LineBreak());
                    break;
            }

            pos = tag.Pos + tag.Length;
        }

        return result;
    }

    private enum TagKind { Italic, Bold, Heading, LineBreak, HorizontalRule }

    private readonly record struct TagMatch(int Pos, int Length, TagKind Kind, int Delta);

    /// <summary>
    /// Linear scan from <paramref name="from"/> for the next occurrence of any supported tag
    /// literal. <c>&lt;br&gt;</c> / <c>&lt;hr&gt;</c> also match their XML self-closing forms.
    /// Returns <c>Pos = -1</c> when none is found.
    /// </summary>
    private static TagMatch FindNextTag(string text, int from)
    {
        var best = new TagMatch(-1, 0, TagKind.Italic, 0);

        void TryTag(string tag, TagKind kind, int delta)
        {
            var idx = text.IndexOf(tag, from, System.StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return;
            // Earliest wins; on a tie the longer literal wins so "<br/>" isn't shadowed by
            // a hypothetical shorter prefix match.
            if (best.Pos >= 0 && (idx > best.Pos || (idx == best.Pos && tag.Length <= best.Length)))
                return;
            best = new TagMatch(idx, tag.Length, kind, delta);
        }

        TryTag("<i>", TagKind.Italic, +1);
        TryTag("</i>", TagKind.Italic, -1);
        TryTag("<b>", TagKind.Bold, +1);
        TryTag("</b>", TagKind.Bold, -1);
        TryTag("<h1>", TagKind.Heading, +1);
        TryTag("</h1>", TagKind.Heading, -1);
        TryTag("<br/>", TagKind.LineBreak, 0);
        TryTag("<br>", TagKind.LineBreak, 0);
        TryTag("<hr/>", TagKind.HorizontalRule, 0);
        TryTag("<hr>", TagKind.HorizontalRule, 0);

        return best;
    }

    private static Run MakeRun(string text, int italicDepth, int boldDepth, int headingDepth)
    {
        var run = new Run(text);
        if (italicDepth > 0) run.FontStyle = FontStyles.Italic;
        if (boldDepth > 0 || headingDepth > 0) run.FontWeight = FontWeights.Bold;
        if (headingDepth > 0)
            // Larger reading-comfort size for the book title. Absolute (not inherited-relative
            // — TextBlock.Inlines runs can't express a relative size) but scales off the OS
            // message-font baseline so it tracks the user's system text-size preference.
            run.FontSize = SystemFonts.MessageFontSize * 1.45;
        return run;
    }
}
