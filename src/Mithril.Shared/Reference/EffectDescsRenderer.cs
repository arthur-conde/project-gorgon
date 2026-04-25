using System.Globalization;

namespace Mithril.Shared.Reference;

/// <summary>
/// Pure projection of raw <see cref="ItemEntry.EffectDescs"/> strings into display-ready
/// <see cref="EffectLine"/> rows. Resolves <c>{TOKEN}{value}</c> placeholders via the
/// supplied <see cref="AttributeEntry"/> registry (attributes.json) and applies the
/// per-attribute <c>DisplayRule</c> / <c>DisplayType</c> semantics.
/// </summary>
public static class EffectDescsRenderer
{
    /// <summary>
    /// Renders each raw effect string to zero or one <see cref="EffectLine"/>:
    /// prose (no braces) passes through; tokenized <c>{TOKEN}{value}</c> rows are
    /// formatted per the attribute registry. Unknown tokens, unresolvable values,
    /// and rows suppressed by their <c>DisplayRule</c> are silently dropped.
    /// </summary>
    public static IReadOnlyList<EffectLine> Render(
        IReadOnlyList<string>? rawDescs,
        IReadOnlyDictionary<string, AttributeEntry> attributes)
    {
        if (rawDescs is null || rawDescs.Count == 0) return [];

        var lines = new List<EffectLine>(rawDescs.Count);
        foreach (var raw in rawDescs)
        {
            if (TryRender(raw, attributes, out var line))
                lines.Add(line);
        }
        return lines;
    }

    private static bool TryRender(string? raw, IReadOnlyDictionary<string, AttributeEntry> attributes, out EffectLine line)
    {
        line = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Prose rows ("Equipping this armor teaches you…") pass through unchanged.
        if (raw.IndexOf('{') < 0)
        {
            line = new EffectLine(0, raw);
            return true;
        }

        // Tokenized rows are `{TOKEN}{value}` — two brace groups. Anything else is malformed.
        var firstOpen = raw.IndexOf('{');
        var firstClose = raw.IndexOf('}', firstOpen + 1);
        if (firstClose < 0) return false;
        var secondOpen = raw.IndexOf('{', firstClose + 1);
        if (secondOpen < 0) return false;
        var secondClose = raw.IndexOf('}', secondOpen + 1);
        if (secondClose < 0) return false;

        var token = raw.Substring(firstOpen + 1, firstClose - firstOpen - 1).Trim();
        var valueText = raw.Substring(secondOpen + 1, secondClose - secondOpen - 1).Trim();
        if (token.Length == 0) return false;
        if (!attributes.TryGetValue(token, out var attr)) return false;
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return false;

        if (!ShouldRender(attr, value)) return false;

        var formatted = FormatValue(attr.DisplayType, value);
        var text = ComposeLine(attr.DisplayType, attr.Label, formatted);
        var icon = attr.IconIds.Count > 0 ? attr.IconIds[0] : 0;
        line = new EffectLine(icon, text);
        return true;
    }

    private static bool ShouldRender(AttributeEntry attr, double value) => attr.DisplayRule switch
    {
        "Always" => true,
        "IfNotZero" => value != 0,
        "IfNotDefault" => attr.DefaultValue is not double def || Math.Abs(value - def) > double.Epsilon,
        _ => true,
    };

    private static string FormatValue(string displayType, double value) => displayType switch
    {
        "AsInt" => ((int)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
        "AsBuffDelta" => value.ToString("+0;-0;0", CultureInfo.InvariantCulture),
        // Multiplier semantics: 1.08 → +8%.
        "AsBuffMod" => ((value - 1) * 100).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "%",
        "AsPercent" => (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%",
        "AsDoubleTimes100" => (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("0.##", CultureInfo.InvariantCulture),
    };

    private static string ComposeLine(string displayType, string label, string formatted) => displayType switch
    {
        // Leading-sign / percent reads better as "+12 Lycanthropy Damage" / "8% Knife Damage".
        "AsBuffDelta" or "AsBuffMod" or "AsPercent" or "AsDoubleTimes100" => $"{formatted} {label}",
        // Flat integers read more naturally as "Max Armor: 49".
        _ => $"{label}: {formatted}",
    };
}
