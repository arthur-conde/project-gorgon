using FluentAssertions;
using Mithril.Shared.Hotkeys;
using Xunit;

namespace Mithril.Shared.Tests;

public class HotkeyServiceShouldRegisterTests
{
    [Theory]
    [InlineData(true, true, true)]    // gate open, command respects gate -> register
    [InlineData(false, true, false)]  // gate closed, command respects gate -> skip
    [InlineData(false, false, true)]  // gate closed, command opts out -> register
    [InlineData(true, false, true)]   // gate open, command opts out -> register
    public void ShouldRegister_TruthTable(bool gateCanFire, bool respectsGate, bool expected)
    {
        var command = new FakeCommand(respectsGate);
        HotkeyService.ShouldRegister(command, gateCanFire).Should().Be(expected);
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
