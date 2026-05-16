using System.Diagnostics;
using System.IO;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #365 Layer 2 guard. Launches the built <c>Mithril.exe --selfcheck</c> out of
/// process: it composes the <em>real</em> 12-module shell DI graph and resolves
/// every startup root under a hard watchdog (no hosted services / CDN). A
/// re-entrant factory-lambda cycle (#359's pathology) does not throw — it hangs —
/// so the contract is exit code, not an exception:
/// <list type="bullet">
/// <item><c>0</c> — full graph resolved, no cycle (the regression guard).</item>
/// <item><c>2</c> — watchdog timeout, i.e. the silent-deadlock signature.</item>
/// </list>
/// Out-of-process isolation is deliberate: it sandboxes the WPF
/// <c>Application</c> singleton / STA teardown and makes a hang hard-killable
/// instead of wedging the test runner.
/// </summary>
[Trait("Category", "Integration")]
public class ShellSelfCheckGuardTests
{
    private const string Config =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string ShellExePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mithril.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run inside the repo tree (Mithril.slnx not found walking up)");
        var exe = Path.Combine(dir!.FullName, "src", "Mithril.Shell", "bin", Config,
            "net10.0-windows", "Mithril.exe");
        File.Exists(exe).Should().BeTrue(
            $"the solution must be built before this test ({exe} missing — run `dotnet build Mithril.slnx`)");
        return exe;
    }

    private static (int ExitCode, string Output) RunSelfCheck(
        int selfCheckTimeoutSeconds, int waitMs, (string, string)? env = null)
    {
        var psi = new ProcessStartInfo(ShellExePath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--selfcheck");
        psi.ArgumentList.Add("--selfcheck-timeout-seconds");
        psi.ArgumentList.Add(selfCheckTimeoutSeconds.ToString());
        if (env is { } e) psi.Environment[e.Item1] = e.Item2;

        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, a) => { if (a.Data is not null) sb.AppendLine(a.Data); };
        p.ErrorDataReceived += (_, a) => { if (a.Data is not null) sb.AppendLine(a.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit(waitMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new Xunit.Sdk.XunitException(
                $"selfcheck process did not exit within {waitMs}ms even though its own watchdog " +
                $"was {selfCheckTimeoutSeconds}s — the watchdog itself failed to fire.\n{sb}");
        }
        p.WaitForExit(); // flush async readers
        return (p.ExitCode, sb.ToString());
    }

    [Fact]
    public void Real_full_module_graph_resolves_with_no_DI_cycle()
    {
        var (exit, output) = RunSelfCheck(selfCheckTimeoutSeconds: 120, waitMs: 200_000);

        exit.Should().Be(0,
            $"the full shell DI graph must resolve every startup root without a re-entrant " +
            $"cycle (#365). Exit 2 = silent deadlock, 1 = a root threw.\n--- selfcheck output ---\n{output}");
        output.Should().Contain("no DI cycle");
    }

    [Fact]
    public void Watchdog_trips_on_a_re_entrant_factory_cycle()
    {
        // Guard-of-the-guard: an injected mutually-factory-resolving singleton pair
        // (the exact #365 shape) must make the watchdog fire with exit 2 — proving
        // the guard detects the pathology, not merely that a clean graph passes.
        var (exit, output) = RunSelfCheck(
            selfCheckTimeoutSeconds: 25, waitMs: 90_000,
            env: ("MITHRIL_SELFCHECK_SELFTEST_CYCLE", "1"));

        exit.Should().Be(2,
            $"an injected re-entrant factory cycle must trip the watchdog (exit 2), not pass " +
            $"or throw.\n--- selfcheck output ---\n{output}");
    }
}
