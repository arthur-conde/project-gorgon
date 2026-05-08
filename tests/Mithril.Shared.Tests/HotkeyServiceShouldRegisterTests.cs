using FluentAssertions;
using Mithril.Shared.Hotkeys;
using Xunit;

namespace Mithril.Shared.Tests;

public class HotkeyServiceShouldRegisterTests
{
    [Theory]
    [InlineData(true, true, true)]    // gate open, binding respects gate -> register
    [InlineData(false, true, false)]  // gate closed, binding respects gate -> skip
    [InlineData(false, false, true)]  // gate closed, binding doesn't respect gate -> register
    [InlineData(true, false, true)]   // gate open, binding doesn't respect gate -> register
    public void ShouldRegister_TruthTable(bool gateCanFire, bool effectiveRespectsGate, bool expected)
    {
        HotkeyService.ShouldRegister(effectiveRespectsGate, gateCanFire).Should().Be(expected);
    }

    public static TheoryData<bool, bool, bool> EffectiveData() => new()
    {
        // commandRespectsGate, bindingAlwaysOn, expectedEffectiveRespects
        { true,  false, true  }, // dev gates, user did not override -> gates
        { true,  true,  false }, // dev gates, user forced always-on -> stays registered
        { false, false, false }, // dev opted out -> stays registered, AlwaysOn redundant
        { false, true,  false }, // dev opted out, user also forced -> stays registered
    };

    [Theory]
    [MemberData(nameof(EffectiveData))]
    public void EffectiveRespectsFocusGate_FoldsCommandDefaultAndBindingOverride(
        bool commandRespects, bool bindingAlwaysOn, bool expected)
    {
        var command = new FakeCommand(commandRespects);
        var binding = new HotkeyBinding("fake", 65, HotkeyModifiers.Ctrl, AlwaysOn: bindingAlwaysOn);
        HotkeyService.EffectiveRespectsFocusGate(command, binding).Should().Be(expected);
    }

    private sealed class FakeCommand : IHotkeyCommand
    {
        public FakeCommand(bool respectsGate) { RespectsFocusGate = respectsGate; }
        public string Id => "fake";
        public string DisplayName => "Fake";
        public string? Category => null;
        public HotkeyBinding? DefaultBinding => null;
        public bool RespectsFocusGate { get; }
        public Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
