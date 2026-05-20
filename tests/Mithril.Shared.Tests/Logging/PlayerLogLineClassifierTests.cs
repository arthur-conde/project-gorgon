using System.IO;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// L0.5 (#532) line-classifier tests. Drives every line of every per-rule
/// fixture + the merged-corpus through <see cref="PlayerLogLineClassifier"/>
/// and asserts the expected kind/data. The per-rule fixtures are the
/// literal regression guards from the #532 design — each is a small file
/// dedicated to one rule, so test failures localize by filename.
/// </summary>
public sealed class PlayerLogLineClassifierTests
{
    private readonly ITestOutputHelper _output;
    public PlayerLogLineClassifierTests(ITestOutputHelper output) { _output = output; }

    private static string FixturePath(params string[] parts)
    {
        var p = new[] { AppContext.BaseDirectory, "Logging", "Fixtures" }
            .Concat(parts).ToArray();
        return Path.Combine(p);
    }

    private static IEnumerable<string> ReadFixtureLines(string relPath)
    {
        var full = FixturePath(relPath.Split('/'));
        return File.ReadAllLines(full).Where(l => !string.IsNullOrWhiteSpace(l));
    }

    // --- Per-rule fixtures: every line classifies to one expected kind ---

    [Fact]
    public void LocalPlayer_pipe_routes_every_line_in_per_rule_fixture()
    {
        var lines = ReadFixtureLines("per-rule/localplayer-process.log").ToList();
        lines.Should().NotBeEmpty();
        foreach (var line in lines)
        {
            var r = PlayerLogLineClassifier.Classify(line);
            r.Kind.Should().Be(PlayerLogLineClassifier.LineKind.LocalPlayer, because: $"line: {line[..Math.Min(80, line.Length)]}");
            r.DataStart.Should().BeGreaterThan(0);
            // The eaten envelope must be the full `[ts] LocalPlayer: ` prefix.
            line[..r.DataStart].Should().EndWith("LocalPlayer: ");
        }
    }

    [Fact]
    public void Combat_actor_pipe_routes_every_line_in_per_rule_fixture()
    {
        var lines = ReadFixtureLines("per-rule/combat-actor-on.log").ToList();
        lines.Should().NotBeEmpty();
        foreach (var line in lines)
        {
            var r = PlayerLogLineClassifier.Classify(line);
            r.Kind.Should().Be(PlayerLogLineClassifier.LineKind.CombatActor, because: $"line: {line[..Math.Min(80, line.Length)]}");
            r.CombatEntityId.Should().BeGreaterThan(0);
            // Data should start with "On" then an uppercase letter.
            var data = line[r.DataStart..];
            data.Should().StartWith("On");
        }
    }

    [Fact]
    public void Prefix_collision_OnAttack_routes_combat_line_to_combat_and_indented_frame_to_discard()
    {
        // The whole point of this fixture: one `entity_<id>: OnAttackHitMe(...)` line
        // (combat) and one `  at Combatant.OnAttackHitMe (...)` stack frame
        // (indented, must be cheap-discard). A token-prefix shortcut would
        // misclassify either or both.
        var lines = ReadFixtureLines("per-rule/prefix-collision-onattack.log").ToList();
        lines.Should().HaveCountGreaterThanOrEqualTo(2);

        var combat = PlayerLogLineClassifier.Classify(lines[0]);
        combat.Kind.Should().Be(PlayerLogLineClassifier.LineKind.CombatActor);

        var frame = PlayerLogLineClassifier.Classify(lines[1]);
        frame.Kind.Should().Be(PlayerLogLineClassifier.LineKind.Discard);
    }

    [Theory]
    [InlineData("per-rule/entity-skin-teardown.log")]
    [InlineData("per-rule/entity-navmesh-nots.log")]
    [InlineData("per-rule/exception-stackframe-xnamespace.log")]
    [InlineData("per-rule/native-address-frames.log")]
    [InlineData("per-rule/direct3d-continuation.log")]
    [InlineData("per-rule/engine-subsystem-brackets.log")]
    [InlineData("per-rule/asset-loader-noise.log")]
    public void Cheap_discard_fixtures_route_every_line_to_discard(string fixture)
    {
        var lines = ReadFixtureLines(fixture).ToList();
        lines.Should().NotBeEmpty();
        foreach (var line in lines)
        {
            var r = PlayerLogLineClassifier.Classify(line);
            r.Kind.Should().Be(PlayerLogLineClassifier.LineKind.Discard,
                because: $"fixture {fixture} should cheap-discard: \"{line[..Math.Min(100, line.Length)]}\"");
        }
    }

    [Fact]
    public void System_signal_fixture_routes_every_line_to_system_signal_pipe()
    {
        var lines = ReadFixtureLines("per-rule/system-signals.log").ToList();
        lines.Should().NotBeEmpty();
        foreach (var line in lines)
        {
            var r = PlayerLogLineClassifier.Classify(line);
            r.Kind.Should().Be(PlayerLogLineClassifier.LineKind.SystemSignal,
                because: $"line: {line[..Math.Min(100, line.Length)]}");
        }
    }

    [Fact]
    public void System_signal_kinds_are_assigned_correctly()
    {
        // Spot-check that each SystemSignalKind is reachable from the fixture.
        var lines = ReadFixtureLines("per-rule/system-signals.log").ToList();
        var kinds = lines.Select(l => PlayerLogLineClassifier.Classify(l))
            .Where(r => r.Kind == PlayerLogLineClassifier.LineKind.SystemSignal)
            .Select(r => r.SystemKind)
            .Distinct()
            .ToHashSet();

        kinds.Should().Contain(SystemSignalKind.AreaLoading);
        kinds.Should().Contain(SystemSignalKind.LoginBanner);
        kinds.Should().Contain(SystemSignalKind.PlayerAdded);
        kinds.Should().Contain(SystemSignalKind.SessionLifecycle);
    }

    // --- Merged corpus: bucket distribution + anomaly cap ---

    [Fact]
    public void Merged_corpus_routes_within_documented_anomaly_budget()
    {
        var lines = File.ReadAllLines(FixturePath("merged-corpus.log"));
        var buckets = new Dictionary<PlayerLogLineClassifier.LineKind, int>();
        var anomalySamples = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var r = PlayerLogLineClassifier.Classify(line);
            buckets[r.Kind] = buckets.GetValueOrDefault(r.Kind) + 1;
            if (r.Kind == PlayerLogLineClassifier.LineKind.Anomaly && anomalySamples.Count < 30)
                anomalySamples.Add(line[..Math.Min(120, line.Length)]);
        }

        // The whole corpus parsed without throwing. Bucket counts are
        // diagnostic; only the anomaly cap is contractual.
        var anomalyCount = buckets.GetValueOrDefault(PlayerLogLineClassifier.LineKind.Anomaly);
        var nonEmpty = buckets.Values.Sum();

        // Anomaly cap is *generous* in this initial PR per #532's "fixture grows,
        // rules grow opportunistically" framing. Tighten in follow-up PRs as
        // production telemetry surfaces new engine-noise shapes worth absorbing.
        // The classifier's structural invariants are verified by the per-rule
        // fixtures above; this assertion floors total drift on the current
        // merged corpus (which is session-start-heavy, not combat-heavy —
        // per-pipe density assertions belong on the per-rule fixtures).
        // Current implementation: 35 anomalies on the n=1997-line corpus
        // (1.8%). Budget is set well above current so opportunistic fixture
        // additions don't break the test; consciously bump or tighten the
        // rule set when a new capture pushes the number meaningfully higher.
        const int AnomalyBudget = 100;
        anomalyCount.Should().BeLessThanOrEqualTo(AnomalyBudget,
            because: $"anomaly samples (first {anomalySamples.Count} of {anomalyCount}, {nonEmpty} non-empty lines total):\n  "
                + string.Join("\n  ", anomalySamples));

        // System signals must be present — the merged corpus includes the
        // session-start preamble (LOADING LEVEL + login banner +
        // ProcessAddPlayer + EVENT(Ok) lifecycle).
        buckets.GetValueOrDefault(PlayerLogLineClassifier.LineKind.SystemSignal)
            .Should().BeGreaterThan(0);

        // Emit the distribution to the test output so it surfaces in CI
        // and in `dotnet test -v normal` runs — useful when telemetry
        // surfaces a new shape and we want to know the new baseline.
        _output.WriteLine($"Merged corpus ({nonEmpty} non-empty lines):");
        foreach (var k in Enum.GetValues<PlayerLogLineClassifier.LineKind>())
        {
            var c = buckets.GetValueOrDefault(k);
            var pct = nonEmpty > 0 ? c * 100.0 / nonEmpty : 0;
            _output.WriteLine($"  {k,-12} {c,5}  ({pct,5:N1}%)");
        }
    }
}
