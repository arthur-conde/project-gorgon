using System;
using System.IO;
using System.Text;

namespace Mithril.Tools.LogSanitizer;

public sealed class LogSanitizer
{
    private readonly ILogSourceRules _rules;

    public LogSanitizer(ILogSourceRules rules)
    {
        _rules = rules;
    }

    public string Sanitize(string input)
    {
        var registry = new NameRegistry();

        // Pass 1: discover names.
        using (var reader = new StringReader(input))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                _rules.DiscoverNames(line, registry);
            }
        }

        // Pass 2: replace names + scrub paths.
        var result = new StringBuilder(input.Length);
        using (var reader = new StringReader(input))
        {
            string? line;
            var first = true;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!first) result.Append('\n');
                first = false;
                result.Append(ApplyReplacements(line, registry));
            }
        }
        return result.ToString();
    }

    private static string ApplyReplacements(string line, NameRegistry registry)
    {
        var working = line;
        foreach (var (name, token) in registry.AllMappings)
        {
            working = working.Replace(name, token, StringComparison.Ordinal);
        }
        return WindowsUsernameScrubber.Scrub(working);
    }
}
