using System.Text;

namespace Silmarillion.ViewModels;

/// <summary>
/// Strips Unity rich-text <c>&lt;color=…&gt;…&lt;/color&gt;</c> spans from a
/// <see cref="Mithril.Reference.Models.Misc.PlayerTitle.Title"/> label.
/// <para>
/// <b>#248 Option A.</b> ~All bundled player-title labels are wrapped in a single
/// <c>&lt;color=name&gt;</c> / <c>&lt;color=#rrggbb&gt;</c> span (e.g.
/// <c>&lt;color=cyan&gt;Game Admin&lt;/color&gt;</c>,
/// <c>&lt;color=#00cc00&gt;Warsmith&lt;/color&gt;</c>). The shared
/// <c>FormattedText</c> renderer (extended in #247 for
/// <c>&lt;i&gt;/&lt;b&gt;/&lt;h1&gt;/&lt;hr&gt;/&lt;br&gt;</c>) does <i>not</i>
/// parse <c>&lt;color&gt;</c>, so the master list / detail header would otherwise
/// show the literal markup. Rendering the colour (Option B) means a colour-name →
/// <c>Brush</c> table baked into a shared renderer used by every long-form
/// surface — a disproportionate blast radius for a low-payoff long-tail tab. The
/// colour is cosmetic flair, not information, so for v1 we strip it in the row /
/// detail projection (localised, ~tag-stripping only). Revisit colour rendering as
/// a separate polish issue if desired.
/// </para>
/// <para>
/// Drift-safe: only the literal <c>&lt;color</c>…<c>&gt;</c> open tag and the
/// <c>&lt;/color&gt;</c> close tag are removed; any other markup (or an
/// unbalanced / malformed colour tag) passes through verbatim rather than
/// throwing — same forgiving contract as the <c>FormattedText</c> renderer's
/// unbalanced-tag handling. Inner text (including any nested <c>&lt;b&gt;</c>
/// etc.) is preserved so the body still flows through the shared renderer.
/// </para>
/// </summary>
public static class TitleColorMarkup
{
    /// <summary>
    /// Returns <paramref name="raw"/> with every well-formed <c>&lt;color=…&gt;</c>
    /// open tag and <c>&lt;/color&gt;</c> close tag removed, preserving all inner
    /// text. Returns the input unchanged when null/empty or when it carries no
    /// colour tag (cheap fast-path). A lone unmatched open/close tag is still
    /// stripped (the label is cleaner without it; the surrounding renderer is
    /// already tolerant of unbalanced markup).
    /// </summary>
    public static string? Strip(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        // Fast-path: no colour tag at all → return the original instance untouched.
        if (raw.IndexOf("<color", System.StringComparison.OrdinalIgnoreCase) < 0
            && raw.IndexOf("</color>", System.StringComparison.OrdinalIgnoreCase) < 0)
        {
            return raw;
        }

        var sb = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            // <color ... >  — open tag (with or without an '=' value). Skip to the
            // closing '>'. A '<color' that never closes ('<colorabc' with no '>')
            // is treated as literal text (defensive — don't swallow the tail).
            if (MatchesAt(raw, i, "<color"))
            {
                var close = raw.IndexOf('>', i);
                if (close >= 0)
                {
                    i = close + 1;
                    continue;
                }
            }
            // </color>
            if (MatchesAt(raw, i, "</color>"))
            {
                i += "</color>".Length;
                continue;
            }
            sb.Append(raw[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool MatchesAt(string s, int index, string token)
    {
        if (index + token.Length > s.Length) return false;
        return string.Compare(s, index, token, 0, token.Length,
            System.StringComparison.OrdinalIgnoreCase) == 0;
    }
}
