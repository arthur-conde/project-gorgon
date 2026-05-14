using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Attached property + parser that renders Project Gorgon's tiny inline-markup vocabulary
/// (<c>&lt;i&gt;…&lt;/i&gt;</c>, <c>&lt;b&gt;…&lt;/b&gt;</c>) into a <see cref="TextBlock"/>'s
/// <see cref="TextBlock.Inlines"/> collection. Quest descriptions, lorebook bodies and item
/// flavor text all use this two-tag subset — most commonly as a speaker prefix
/// (<c>&lt;i&gt;Joeh:&lt;/i&gt; Hello adventurer.</c>). A plain <c>TextBlock.Text</c> binding
/// renders the tags literally; this property parses them into italic / bold runs so the chip
/// reads as intended.
/// <para>
/// The parser handles nesting (<c>&lt;b&gt;&lt;i&gt;X&lt;/i&gt;&lt;/b&gt;</c>) via formatting
/// counts; unbalanced or interleaved tags close the appropriate counter without throwing, so
/// data drift can't crash the renderer. Bare <c>&lt;</c> characters not followed by one of the
/// four supported tags pass through as literal text.
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
    /// Tokenise <paramref name="text"/> and yield the WPF <see cref="Inline"/> runs that
    /// would render it with the embedded italic / bold spans applied. Pure function — usable
    /// from tests without instantiating a TextBlock.
    /// </summary>
    public static IReadOnlyList<Inline> BuildInlines(string text)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(text)) return result;

        // Counts rather than a stack so unbalanced/interleaved data drift can't crash —
        // each </tag> decrements (clamped at 0), each <tag> increments. A run inherits the
        // current italic/bold state at emission time.
        var italicDepth = 0;
        var boldDepth = 0;
        var pos = 0;

        while (pos < text.Length)
        {
            var (tagPos, tagLength, italicDelta, boldDelta) = FindNextTag(text, pos);

            if (tagPos < 0)
            {
                var tail = text.Substring(pos);
                if (tail.Length > 0) result.Add(MakeRun(tail, italicDepth, boldDepth));
                break;
            }

            if (tagPos > pos)
            {
                var segment = text.Substring(pos, tagPos - pos);
                result.Add(MakeRun(segment, italicDepth, boldDepth));
            }

            italicDepth = System.Math.Max(0, italicDepth + italicDelta);
            boldDepth = System.Math.Max(0, boldDepth + boldDelta);
            pos = tagPos + tagLength;
        }

        return result;
    }

    /// <summary>
    /// Linear scan from <paramref name="from"/> for the next occurrence of any of the four
    /// supported tag literals. Returns -1 in <c>Pos</c> when none is found.
    /// </summary>
    private static (int Pos, int Length, int ItalicDelta, int BoldDelta) FindNextTag(string text, int from)
    {
        var bestPos = -1;
        var bestLen = 0;
        var bestItalic = 0;
        var bestBold = 0;

        TryTag("<i>", italicDelta: +1, boldDelta: 0);
        TryTag("</i>", italicDelta: -1, boldDelta: 0);
        TryTag("<b>", italicDelta: 0, boldDelta: +1);
        TryTag("</b>", italicDelta: 0, boldDelta: -1);

        return (bestPos, bestLen, bestItalic, bestBold);

        void TryTag(string tag, int italicDelta, int boldDelta)
        {
            var idx = text.IndexOf(tag, from, System.StringComparison.Ordinal);
            if (idx < 0) return;
            if (bestPos >= 0 && idx >= bestPos) return;
            bestPos = idx;
            bestLen = tag.Length;
            bestItalic = italicDelta;
            bestBold = boldDelta;
        }
    }

    private static Run MakeRun(string text, int italicDepth, int boldDepth)
    {
        var run = new Run(text);
        if (italicDepth > 0) run.FontStyle = FontStyles.Italic;
        if (boldDepth > 0) run.FontWeight = FontWeights.Bold;
        return run;
    }
}
