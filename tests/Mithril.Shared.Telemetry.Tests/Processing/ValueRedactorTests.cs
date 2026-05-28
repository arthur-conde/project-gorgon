using FluentAssertions;
using Mithril.Shared.Telemetry.Processing;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Processing;

public class ValueRedactorTests
{
    [Fact]
    public void Redacts_user_profile_prefix()
    {
        var r = new ValueRedactor(getActiveCharacter: () => null,
                                  userProfile: @"C:\Users\alice",
                                  localAppData: @"C:\Users\alice\AppData\Local");
        r.Redact(@"C:\Users\alice\Documents\foo.log").Should().Be(@"$USER\Documents\foo.log");
    }

    [Fact]
    public void Redacts_local_app_data_prefix()
    {
        var r = new ValueRedactor(getActiveCharacter: () => null,
                                  userProfile: @"C:\Users\alice",
                                  localAppData: @"C:\Users\alice\AppData\Local");
        r.Redact(@"C:\Users\alice\AppData\Local\Mithril\shell.json").Should().Be(@"$LOCALAPPDATA\Mithril\shell.json");
    }

    [Fact]
    public void Redacts_active_character_name_case_insensitive()
    {
        var r = new ValueRedactor(getActiveCharacter: () => "Thorgrim", userProfile: "C:\\nope", localAppData: "C:\\nope");
        r.Redact("Player thorgrim picked up Iron Ore").Should().Be("Player $CHARACTER picked up Iron Ore");
    }

    [Fact]
    public void No_op_when_no_match()
    {
        var r = new ValueRedactor(getActiveCharacter: () => "Thorgrim", userProfile: @"C:\Users\alice", localAppData: @"C:\Users\alice\AppData\Local");
        r.Redact("plain string, no PII").Should().Be("plain string, no PII");
    }

    [Fact]
    public void No_active_character_no_substitution()
    {
        var r = new ValueRedactor(getActiveCharacter: () => null, userProfile: @"C:\Users\alice", localAppData: @"C:\Users\alice\AppData\Local");
        r.Redact("Thorgrim").Should().Be("Thorgrim");
    }

    [Fact]
    public void Returns_input_unchanged_for_null_or_empty()
    {
        var r = new ValueRedactor(getActiveCharacter: () => "Thorgrim", userProfile: "x", localAppData: "x");
        r.Redact(null).Should().BeNull();
        r.Redact("").Should().Be("");
    }
}
