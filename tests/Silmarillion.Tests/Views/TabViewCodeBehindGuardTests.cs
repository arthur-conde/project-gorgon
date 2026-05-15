using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace Silmarillion.Tests.Views;

/// <summary>
/// Source-level invariant guard. Every Silmarillion <c>*.xaml</c> that declares an
/// <c>x:Class</c> MUST ship a hand-authored <c>*.xaml.cs</c> code-behind whose
/// constructor calls <c>InitializeComponent()</c>.
/// <para>
/// If the code-behind is missing, the XAML compiler still emits the partial class
/// (with <c>InitializeComponent</c>) in <c>obj/**/*.g.cs</c>, so the build SUCCEEDS
/// and every unit test stays green — but the implicit default constructor never
/// calls <c>InitializeComponent()</c>, so the control instantiates EMPTY at runtime
/// (blank tab/pane, no exception). This silently shipped <c>LorebooksTabView</c> and
/// <c>StorageVaultsTabView</c> on <c>main</c>. xunit never instantiates XAML, so a
/// source-scan is the cheapest layer that actually catches this class of bug.
/// </para>
/// </summary>
public sealed class TabViewCodeBehindGuardTests
{
    [Fact]
    public void EveryXamlWithXClass_HasCodeBehindCallingInitializeComponent()
    {
        var viewsDir = FindViewsDir();
        if (viewsDir is null) return; // source tree not reachable (packaged run) — skip, don't false-fail

        var offenders = Directory
            .EnumerateFiles(viewsDir, "*.xaml", SearchOption.AllDirectories)
            .Where(xaml => File.ReadAllText(xaml).Contains("x:Class=\""))
            .Where(xaml =>
            {
                var codeBehind = xaml + ".cs";
                return !File.Exists(codeBehind)
                       || !File.ReadAllText(codeBehind).Contains("InitializeComponent(");
            })
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        offenders.Should().BeEmpty(
            "every x:Class XAML needs a .xaml.cs calling InitializeComponent() or the "
            + "control renders blank at runtime while the build/tests stay green");
    }

    /// <summary>
    /// Walk up from the test bin dir to the repo root (marked by <c>Mithril.slnx</c>),
    /// then resolve <c>src/Silmarillion.Module/Views</c>. Returns null if not found.
    /// </summary>
    private static string? FindViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mithril.slnx")))
            dir = dir.Parent;

        if (dir is null) return null;
        var views = Path.Combine(dir.FullName, "src", "Silmarillion.Module", "Views");
        return Directory.Exists(views) ? views : null;
    }
}
