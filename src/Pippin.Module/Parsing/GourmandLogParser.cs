using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Pippin.Parsing;

public sealed partial class GourmandLogParser : ILogParser
{
    // Match the ProcessBook line containing the Foods Consumed report.
    // The body uses literal \n escapes (not real newlines) in the log file.
    [GeneratedRegex(@"LocalPlayer:\s*ProcessBook\(""Skill Info"",\s*""(Foods Consumed:\\n\\n(?:[^""\\]|\\.)*)"".*""SkillReport""")]
    private static partial Regex ProcessBookFoodsRx();

    // Match a single food entry line: "  FoodName (HAS TAG, HAS TAG): count"
    [GeneratedRegex(@"^\s+(.+?)(?:\s+\(([^)]+)\))?\s*:\s*(\d+)$")]
    private static partial Regex FoodEntryRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        // Fast path: skip lines that can't possibly match
        if (!line.Contains("ProcessBook", StringComparison.Ordinal))
            return null;

        var outer = ProcessBookFoodsRx().Match(line);
        if (!outer.Success) return null;

        var body = outer.Groups[1].Value;
        // The log encodes newlines as literal two-char sequences: backslash + n
        var entries = body.Split("\\n", StringSplitOptions.RemoveEmptyEntries);

        var foods = new List<FoodConsumedEntry>();
        foreach (var entry in entries)
        {
            var m = FoodEntryRx().Match(entry);
            if (!m.Success) continue;

            var name = m.Groups[1].Value.Trim();
            var count = int.Parse(m.Groups[3].Value);

            var tags = Array.Empty<string>();
            if (m.Groups[2].Success)
            {
                tags = m.Groups[2].Value
                    .Split(',', StringSplitOptions.TrimEntries)
                    .Select(t => t.StartsWith("HAS ", StringComparison.OrdinalIgnoreCase)
                        ? t[4..].Trim()
                        : t.Trim())
                    .ToArray();
            }

            foods.Add(new FoodConsumedEntry(name, count, tags));
        }

        return foods.Count > 0
            ? new FoodsConsumedReport(timestamp, foods)
            : null;
    }
}
