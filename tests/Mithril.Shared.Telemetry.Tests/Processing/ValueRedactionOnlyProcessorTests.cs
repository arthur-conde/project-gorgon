using System;
using System.Diagnostics;
using FluentAssertions;
using Mithril.Shared.Telemetry.Processing;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Processing;

/// <summary>
/// Behavioural contract for the TrustEndpoint = true processor (mithril#840):
/// unknown tag keys survive (no catalog gate, no allowlist), Sensitive-classified
/// tags survive (no default-off), user-disabled overrides are ignored — but the
/// <see cref="ValueRedactor"/> still scrubs path prefixes and the active
/// character name from string tag values.
///
/// Tests bypass the process-global <see cref="ActivityListener"/> registry by
/// calling <see cref="ValueRedactionOnlyProcessor.OnEnd"/> directly on a
/// hand-built <see cref="Activity"/>. Routing through
/// <see cref="ActivitySource.StartActivity"/> would let any other test class's
/// listener (running in parallel) observe and mutate the same Activity — and
/// the existing <c>AllowlistAndRedactionProcessorTests</c> registers listeners
/// with <c>ShouldListenTo = _ =&gt; true</c>, which would drop our unknown-tag
/// assertions out from under us.
/// </summary>
public class ValueRedactionOnlyProcessorTests
{
    private static ValueRedactionOnlyProcessor Build(
        Func<string?>? activeChar = null,
        string userProfile = @"C:\Users\u",
        string localAppData = @"C:\Users\u\AppData\Local")
    {
        var redactor = new ValueRedactor(activeChar ?? (() => null), userProfile, localAppData);
        return new ValueRedactionOnlyProcessor(redactor);
    }

    [Fact]
    public void Keeps_tag_whose_key_is_unknown_to_the_catalog()
    {
        var p = Build();
        using var act = new Activity("op").Start();
        act.SetTag("brand.new.unknown.tag", "value");

        p.OnEnd(act);

        act.GetTagItem("brand.new.unknown.tag").Should().Be("value",
            "TrustEndpoint = true skips the allowlist entirely so producer-emitted tags " +
            "flow to the destination without catalog membership.");
    }

    [Fact]
    public void Keeps_sensitive_classified_tag_that_the_default_processor_would_drop()
    {
        // The full AllowlistAndRedactionProcessor drops Sensitive-classified tags unless
        // explicitly opt-in via user override. The redactor-only processor has no
        // classification awareness at all.
        var p = Build();
        using var act = new Activity("op").Start();
        act.SetTag("character.name", "Thorgrim");

        p.OnEnd(act);

        act.GetTagItem("character.name").Should().Be("Thorgrim");
    }

    [Fact]
    public void Redacts_userprofile_path_prefix_from_string_tag_value()
    {
        var p = Build();
        using var act = new Activity("op").Start();
        act.SetTag("unknown.path.tag", @"C:\Users\u\Documents\save.log");

        p.OnEnd(act);

        // Belt-and-suspenders: the redactor still rewrites %USERPROFILE%-rooted
        // paths even when the allowlist is bypassed. The screenshot-leak threat
        // is independent of whether the destination is "trusted".
        act.GetTagItem("unknown.path.tag").Should().Be(@"$USER\Documents\save.log");
    }

    [Fact]
    public void Redacts_localappdata_prefix_before_userprofile_so_longer_prefix_wins()
    {
        var p = Build();
        using var act = new Activity("op").Start();
        act.SetTag("unknown.path.tag", @"C:\Users\u\AppData\Local\Mithril\boot.log");

        p.OnEnd(act);

        // ValueRedactor applies $LOCALAPPDATA before $USER so the longer prefix
        // wins; we keep that invariant in trust-mode as well.
        act.GetTagItem("unknown.path.tag").Should().Be(@"$LOCALAPPDATA\Mithril\boot.log");
    }

    [Fact]
    public void Redacts_active_character_name_from_string_tag_value()
    {
        var p = Build(activeChar: () => "Thorgrim");
        using var act = new Activity("op").Start();
        act.SetTag("error.message", "Thorgrim died");

        p.OnEnd(act);

        act.GetTagItem("error.message").Should().Be("$CHARACTER died");
    }

    [Fact]
    public void Leaves_non_string_tag_values_alone()
    {
        var p = Build();
        using var act = new Activity("op").Start();
        act.SetTag("count", 42);
        act.SetTag("ratio", 0.5);

        p.OnEnd(act);

        act.GetTagItem("count").Should().Be(42);
        act.GetTagItem("ratio").Should().Be(0.5);
    }
}
