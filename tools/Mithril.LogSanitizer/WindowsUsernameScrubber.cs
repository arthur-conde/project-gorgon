using System.Text.RegularExpressions;

namespace Mithril.Tools.LogSanitizer;

public static partial class WindowsUsernameScrubber
{
    // Matches "Users\<name>\" or "Users/<name>/" — captures <name>.
    // <name> = any non-separator chars (excludes \ / : * ? " < > | so we don't eat past the segment).
    [GeneratedRegex(@"Users([\\/])[^\\/:*?""<>|]+([\\/])", RegexOptions.CultureInvariant)]
    private static partial Regex UsersPathPattern();

    public static string Scrub(string input)
    {
        return UsersPathPattern().Replace(input, "Users$1<USER>$2");
    }
}
