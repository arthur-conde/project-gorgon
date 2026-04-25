// Validates that every UserControl/Window/Page XAML self-contains its
// {StaticResource X} dependencies via UserControl.Resources/MergedDictionaries
// instead of relying on Application.Resources (App.xaml) being merged before
// the control is parsed.
//
// Why: {StaticResource} resolves at XAML parse time. When a UserControl is
// constructed before being added to a tree (DI factory lambdas, designer,
// alternate hosts, forks that don't carry App.xaml's merges), only its own
// Resources + Application.Resources are visible. Any view loaded outside the
// shell's App.Run() flow throws "Cannot find resource named 'X'" at
// InitializeComponent.
//
// Usage: XamlResourceLint <repo-root>
//   exits 0 with a one-line summary if all references resolve
//   exits 1 with MSBuild-format diagnostics if any do not
//   exits 2 on bad invocation

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: XamlResourceLint <repo-root>");
    return 2;
}

var repoRoot = Path.GetFullPath(args[0]);
var srcRoot = Path.Combine(repoRoot, "src");
if (!Directory.Exists(srcRoot))
{
    Console.Error.WriteLine($"src directory not found at {srcRoot}");
    return 2;
}

// 1. Walk every Resources.xaml under src/ and record key → defining pack URI.
//    These are the canonical merge sources; views must merge one of these to
//    legally reference its keys via {StaticResource}.
var keyDefs = new Dictionary<string, List<(string PackUri, string File)>>(StringComparer.Ordinal);
foreach (var resFile in EnumerateXaml(srcRoot))
{
    if (!IsResourceDictionaryFile(resFile)) continue;
    var pack = ToPackUri(resFile, srcRoot);
    if (pack is null) continue;
    foreach (var key in ExtractDefinedKeys(resFile))
    {
        if (!keyDefs.TryGetValue(key, out var list))
            keyDefs[key] = list = new();
        list.Add((pack, resFile));
    }
}

// 2. Walk every non-resource view XAML and validate its StaticResource refs.
var staticRefRe = new Regex(@"\{\s*(?:StaticResource|StaticResourceExtension)\s+([A-Za-z_][\w.]*)\s*\}", RegexOptions.Compiled);
var failures = new List<string>();
foreach (var xaml in EnumerateXaml(srcRoot))
{
    if (IsResourceDictionaryFile(xaml)) continue;
    var name = Path.GetFileName(xaml);
    if (string.Equals(name, "App.xaml", StringComparison.OrdinalIgnoreCase)) continue;

    var text = File.ReadAllText(xaml);
    var localKeys = ExtractDefinedKeys(xaml);
    var mergedPacks = ExtractMergedDictionaryPacks(xaml)
        .Select(s => NormalizeSourceToPackUri(s, xaml, srcRoot))
        .Where(s => s is not null)
        .Select(s => s!)
        .ToList();

    var lines = text.Split('\n');
    var seenInThisFile = new HashSet<string>(StringComparer.Ordinal);
    for (int i = 0; i < lines.Length; i++)
    {
        foreach (Match m in staticRefRe.Matches(lines[i]))
        {
            var key = m.Groups[1].Value;
            if (localKeys.Contains(key)) continue;
            if (!keyDefs.TryGetValue(key, out var defs)) continue; // unknown — likely registered in code-behind, can't validate
            if (defs.Any(d => mergedPacks.Any(mp => PackUrisEqual(mp, d.PackUri)))) continue;
            if (!seenInThisFile.Add(key)) continue; // one diagnostic per (file, key) is enough

            var defList = string.Join(", ", defs.Select(d => Path.GetRelativePath(repoRoot, d.File).Replace('\\', '/')));
            var suggestPack = defs[0].PackUri;
            var rel = Path.GetRelativePath(repoRoot, xaml).Replace('\\', '/');

            failures.Add(
                $"{rel}({i + 1},1): error XRES001: " +
                $"`{{StaticResource {key}}}` resolves only from {defList}, but this view does not merge that dictionary at UserControl scope. " +
                $"At parse time it relies on Application.Resources being merged first (App.xaml), which fails for views constructed outside the shell's startup flow. " +
                $"Fix: add a UserControl.Resources block merging \"{suggestPack}\". " +
                $"Pattern: src/Samwise.Module/Views/GardenView.xaml. " +
                $"Or: define `{key}` locally in this file's <UserControl.Resources>.");
        }
    }
}

if (failures.Count == 0)
{
    Console.WriteLine($"XamlResourceLint: OK ({keyDefs.Count} keys across resource dictionaries).");
    return 0;
}

foreach (var f in failures) Console.Error.WriteLine(f);
Console.Error.WriteLine($"XamlResourceLint: {failures.Count} unresolved {{StaticResource}} reference(s). See XRES001 above.");
return 1;

static IEnumerable<string> EnumerateXaml(string root) =>
    Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                 && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

static bool IsResourceDictionaryFile(string path) =>
    string.Equals(Path.GetFileName(path), "Resources.xaml", StringComparison.OrdinalIgnoreCase);

static HashSet<string> ExtractDefinedKeys(string xamlPath)
{
    var keys = new HashSet<string>(StringComparer.Ordinal);
    try
    {
        var doc = XDocument.Load(xamlPath);
        XNamespace xNs = XamlNs;
        foreach (var e in doc.Descendants())
        {
            var k = e.Attribute(xNs + "Key");
            if (k is not null) keys.Add(k.Value);
        }
    }
    catch (XmlException) { }
    return keys;
}

static List<string> ExtractMergedDictionaryPacks(string xamlPath)
{
    var result = new List<string>();
    try
    {
        var doc = XDocument.Load(xamlPath);
        foreach (var rd in doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary"))
        {
            var src = rd.Attribute("Source")?.Value;
            if (!string.IsNullOrWhiteSpace(src)) result.Add(src.Trim());
        }
    }
    catch (XmlException) { }
    return result;
}

static string? ToPackUri(string resourceFile, string srcRoot)
{
    var rel = Path.GetRelativePath(srcRoot, resourceFile).Replace('\\', '/');
    var firstSlash = rel.IndexOf('/');
    if (firstSlash < 0) return null;
    var asm = rel[..firstSlash];
    var inside = rel[(firstSlash + 1)..];
    return $"pack://application:,,,/{asm};component/{inside}";
}

// Normalize a `<ResourceDictionary Source="...">` value to its canonical pack URI.
// Accepts the explicit pack form, plain relative paths (`Resources.xaml`,
// `../Foo/Resources.xaml`), and the same-assembly component form
// (`/Module;component/Views/Resources.xaml`). Returns null if the value points
// outside src/ (third-party theme files, etc.).
static string? NormalizeSourceToPackUri(string source, string consumingFile, string srcRoot)
{
    if (string.IsNullOrWhiteSpace(source)) return null;
    var trimmed = source.Trim();

    if (trimmed.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        return trimmed;

    string? resolvedPath;
    if (Path.IsPathRooted(trimmed) || trimmed.StartsWith("/"))
    {
        // /Assembly;component/Path/To.xaml — same-assembly component form.
        // Translate to a file path under srcRoot so it can be canonicalized.
        var componentMarker = ";component/";
        var idx = trimmed.IndexOf(componentMarker, StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            var asm = trimmed.TrimStart('/').Substring(0, trimmed.TrimStart('/').IndexOf(';'));
            var inside = trimmed.Substring(idx + componentMarker.Length);
            return $"pack://application:,,,/{asm};component/{inside}";
        }
        resolvedPath = trimmed;
    }
    else
    {
        var dir = Path.GetDirectoryName(consumingFile)!;
        resolvedPath = Path.GetFullPath(Path.Combine(dir, trimmed));
    }

    if (!resolvedPath.StartsWith(srcRoot, StringComparison.OrdinalIgnoreCase))
        return null;

    return ToPackUri(resolvedPath, srcRoot);
}

static bool PackUrisEqual(string a, string b) =>
    string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
