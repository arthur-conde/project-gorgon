using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.Shared.Tests.Game;

/// <summary>
/// #959: <see cref="GameLocator.AutoDetectInstallRoot"/> resolves the Steam INSTALL
/// dir for the asset-extractor sidecar. These tests exercise the two pure helpers
/// (<c>ParseLibraryRoots</c> over a sample <c>libraryfolders.vdf</c> string, and
/// <c>ResolvePgInstall</c> against a temp-dir tree) so we cover the logic without
/// touching the live registry/Steam. The public entry point stays fail-soft.
/// </summary>
public sealed class GameLocatorInstallRootTests : IDisposable
{
    private readonly string _tempRoot;

    public GameLocatorInstallRootTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mithril-gamelocator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    private static string Vdf(string lib0, string lib1) =>
        "\"libraryfolders\"\n{\n" +
        "  \"0\"\n  {\n" +
        $"    \"path\"\t\t\"{lib0.Replace("\\", "\\\\")}\"\n" +
        "    \"apps\"\n    {\n      \"228980\"\t\"100\"\n    }\n  }\n" +
        "  \"1\"\n  {\n" +
        $"    \"path\"\t\t\"{lib1.Replace("\\", "\\\\")}\"\n" +
        "    \"apps\"\n    {\n      \"342940\"\t\"5000\"\n      \"413150\"\t\"200\"\n    }\n  }\n" +
        "}\n";

    private string MakePgTree(string libRoot)
    {
        var install = Path.Combine(libRoot, "steamapps", "common", "Project Gorgon");
        var streamingAssets = Path.Combine(
            install, "WindowsPlayer_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        Directory.CreateDirectory(streamingAssets);
        return install;
    }

    [Fact]
    public void ParseLibraryRoots_returns_only_libraries_listing_pg_appid()
    {
        var libWithoutPg = @"D:\SteamLibraryA";
        var libWithPg = @"E:\SteamLibraryB";

        var roots = GameLocator.ParseLibraryRoots(Vdf(libWithoutPg, libWithPg));

        roots.Should().ContainSingle().Which.Should().Be(libWithPg);
    }

    [Fact]
    public void ResolvePgInstall_returns_install_dir_when_streaming_assets_present()
    {
        var lib = Path.Combine(_tempRoot, "lib");
        var expectedInstall = MakePgTree(lib);

        var resolved = GameLocator.ResolvePgInstall(new[] { lib });

        resolved.Should().Be(expectedInstall);
    }

    [Fact]
    public void ResolvePgInstall_returns_null_when_streaming_assets_absent()
    {
        // A library root with the common\Project Gorgon dir but WITHOUT the
        // StreamingAssets proof should NOT verify.
        var lib = Path.Combine(_tempRoot, "lib");
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common", "Project Gorgon"));

        GameLocator.ResolvePgInstall(new[] { lib }).Should().BeNull();
    }

    [Fact]
    public void ResolvePgInstall_returns_null_for_empty_candidate_set()
        => GameLocator.ResolvePgInstall(System.Array.Empty<string>()).Should().BeNull();

    [Fact]
    public void ResolvePgInstall_picks_first_verified_library()
    {
        var libBad = Path.Combine(_tempRoot, "bad"); // no tree
        var libGood = Path.Combine(_tempRoot, "good");
        var expectedInstall = MakePgTree(libGood);

        var resolved = GameLocator.ResolvePgInstall(new List<string> { libBad, libGood });

        resolved.Should().Be(expectedInstall);
    }

    [Fact]
    public void ParseLibraryRoots_on_garbage_returns_empty_not_throw()
        => GameLocator.ParseLibraryRoots("not a vdf {{{ \"").Should().BeEmpty();
}
