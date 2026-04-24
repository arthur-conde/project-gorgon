using System;
using System.IO;

namespace Gorgon.TestSupport;

/// <summary>
/// Workspace-relative scratch directories for tests. Defender / Search indexer
/// aggressively scan freshly closed files in <c>%TEMP%</c> on Windows, which under
/// parallel test load creates sharing-violations and transient quarantines that
/// push past <c>AtomicFile</c>'s retry budget. Routing test scratch into a
/// repo-relative <c>tests/.tmp/</c> tree sidesteps those heuristics entirely —
/// Defender doesn't apply the same scanning aggression to non-temp paths.
/// </summary>
/// <remarks>
/// The root is resolved by walking up from the test assembly's bin/ directory
/// looking for <c>Gorgon.slnx</c> (the repo marker). If that walk fails (e.g.
/// tests run from an unexpected location), the helper falls back to
/// <see cref="Path.GetTempPath"/> so the suite still works — just with the
/// %TEMP%-on-Windows flake risk it had before.
/// <para><c>tests/.tmp/</c> is gitignored. Cleanup remains the test's job; if a
/// test crashes mid-run, leftover scratch dirs stay out of the working tree.</para>
/// </remarks>
internal static class TestPaths
{
    private static readonly string Root = LocateRoot();

    /// <summary>Create a fresh, unique scratch directory under
    /// <c>tests/.tmp/&lt;prefix&gt;_&lt;guid&gt;/</c> and return its absolute path.</summary>
    public static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Root, $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LocateRoot()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            if (File.Exists(Path.Combine(probe.FullName, "Gorgon.slnx")))
            {
                var root = Path.Combine(probe.FullName, "tests", ".tmp");
                Directory.CreateDirectory(root);
                return root;
            }
            probe = probe.Parent;
        }
        // Fallback if invoked from an unexpected working tree.
        var fallback = Path.Combine(Path.GetTempPath(), "gorgon-tests-fallback");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
