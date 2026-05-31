using System.Diagnostics;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Best-effort detection of the installed Project Gorgon version, used to stamp
/// the extracted-asset manifests + key the canonical-hash gate (issue #931).
///
/// <para>PG is a Unity standalone build; the cleanest stable version signal is the
/// player executable's <see cref="FileVersionInfo"/> (ProductVersion /
/// FileVersion). If that's unavailable we return <c>null</c> — the spec's
/// accept-with-warn fallback covers an unknown version, so detection is never
/// release-blocking.</para>
/// </summary>
public static class PgVersionDetector
{
    /// <summary>
    /// Returns the detected PG version string, or <c>null</c> if it can't be
    /// determined. Never throws.
    /// </summary>
    public static string? TryDetect(string pgInstall)
    {
        try
        {
            // PG's Unity player exe. The product/file version is set by the build.
            foreach (var exe in EnumerateCandidateExes(pgInstall))
            {
                if (!File.Exists(exe)) continue;
                var info = FileVersionInfo.GetVersionInfo(exe);
                var v = FirstNonEmpty(info.ProductVersion, info.FileVersion);
                if (!string.IsNullOrWhiteSpace(v) && v != "0.0.0.0")
                    return v.Trim();
            }
        }
        catch
        {
            // Best-effort only — fall through to null.
        }
        return null;
    }

    private static IEnumerable<string> EnumerateCandidateExes(string pgInstall)
    {
        // Known PG player exe names (the StreamingAssets layout uses WindowsPlayer);
        // also sweep any top-level .exe as a fallback.
        yield return Path.Combine(pgInstall, "WindowsPlayer.exe");
        yield return Path.Combine(pgInstall, "ProjectGorgon.exe");
        if (Directory.Exists(pgInstall))
        {
            foreach (var exe in Directory.EnumerateFiles(pgInstall, "*.exe", SearchOption.TopDirectoryOnly))
                yield return exe;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }
}
