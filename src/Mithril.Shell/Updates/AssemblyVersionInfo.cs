using System.IO;
using System.Reflection;

namespace Mithril.Shell.Updates;

public sealed record AssemblyVersionInfo(
    string InformationalVersion,
    string SemanticVersion,
    string? CommitSha,
    DateTimeOffset? BuildTimestampUtc)
{
    public bool HasCommitSha => !string.IsNullOrEmpty(CommitSha);
    public string ShortCommitSha => CommitSha is null ? "" : CommitSha[..Math.Min(10, CommitSha.Length)];

    public static AssemblyVersionInfo FromEntryAssembly() => FromAssembly(Assembly.GetEntryAssembly() ?? typeof(AssemblyVersionInfo).Assembly);

    public static AssemblyVersionInfo FromAssembly(Assembly asm)
    {
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        TryParseCommitSha(info, out var semver, out var sha);

        DateTimeOffset? built = null;
        try
        {
            var path = asm.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                built = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch { }

        return new AssemblyVersionInfo(info, semver, sha, built);
    }

    // Nerdbank.GitVersioning InformationalVersion format: "{semver}+{sha}[.{height}]"
    // Examples: "1.0.3+01b651dca0", "1.0.3+01b651dca0.5", "1.0.3" (release tag / no git).
    internal static bool TryParseCommitSha(string informational, out string semver, out string? sha)
    {
        semver = informational ?? "";
        sha = null;
        if (string.IsNullOrEmpty(informational)) return false;

        var plus = informational.IndexOf('+');
        if (plus < 0) return false;

        semver = informational[..plus];
        var suffix = informational[(plus + 1)..];
        var dot = suffix.IndexOf('.');
        var candidate = dot < 0 ? suffix : suffix[..dot];
        if (candidate.Length == 0 || !IsHex(candidate)) return false;

        sha = candidate;
        return true;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
}
