using Arda.Contracts.State.Health;
using FluentAssertions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class GrammarBreakSignalTests
{
    private static GrammarBreak MakeBreak(string verb = "ProcessAddItem", string hint = "expected long") =>
        new("Player", verb, "LocalPlayer: ProcessAddItem(NOT_A_NUMBER)", "NOT_A_NUMBER", hint, DateTimeOffset.UtcNow);

    [Fact]
    public void Raise_SetsBothChannelsAndFiresBothEvents()
    {
        var signal = new GrammarBreakSignal();
        var raisedFired = 0;
        var observedFired = 0;
        signal.Raised += (_, _) => raisedFired++;
        signal.ObservedBreakChanged += (_, _) => observedFired++;

        signal.Raise(MakeBreak());

        signal.IsRaised.Should().BeTrue();
        signal.HasObservedBreak.Should().BeTrue("Raise is a superset of MarkObserved");
        signal.ObservedCount.Should().Be(1);
        signal.Current.Should().NotBeNull();
        raisedFired.Should().Be(1);
        observedFired.Should().Be(1);
    }

    [Fact]
    public void MarkObserved_SetsObservationChannelOnly()
    {
        var signal = new GrammarBreakSignal();
        var raisedFired = 0;
        var observedFired = 0;
        signal.Raised += (_, _) => raisedFired++;
        signal.ObservedBreakChanged += (_, _) => observedFired++;

        signal.MarkObserved(MakeBreak());

        signal.IsRaised.Should().BeFalse("tolerant-mode observations must not trigger halt");
        signal.HasObservedBreak.Should().BeTrue();
        signal.ObservedCount.Should().Be(1);
        signal.Current.Should().NotBeNull();
        raisedFired.Should().Be(0);
        observedFired.Should().Be(1);
    }

    [Fact]
    public void Raise_TwiceFiresRaisedOnce_ObservedTwice()
    {
        var signal = new GrammarBreakSignal();
        var raisedFired = 0;
        var observedFired = 0;
        signal.Raised += (_, _) => raisedFired++;
        signal.ObservedBreakChanged += (_, _) => observedFired++;

        signal.Raise(MakeBreak("ProcessFirst"));
        signal.Raise(MakeBreak("ProcessSecond"));

        signal.IsRaised.Should().BeTrue();
        signal.ObservedCount.Should().Be(2);
        signal.Current!.Verb.Should().Be("ProcessFirst", "first break captured wins");
        raisedFired.Should().Be(1, "Raised is a transition event, fires once");
        observedFired.Should().Be(2, "every break increments the observation channel");
    }

    [Fact]
    public void MarkObserved_ThenRaise_FlipsHaltWithoutLosingObservedCount()
    {
        var signal = new GrammarBreakSignal();

        signal.MarkObserved(MakeBreak("ProcessFirst"));
        signal.MarkObserved(MakeBreak("ProcessSecond"));
        signal.Raise(MakeBreak("ProcessThird"));

        signal.IsRaised.Should().BeTrue();
        signal.HasObservedBreak.Should().BeTrue();
        signal.ObservedCount.Should().Be(3);
        signal.Current!.Verb.Should().Be("ProcessFirst", "first-call wins for Current");
    }

    [Fact]
    public void Raise_ThenMarkObserved_KeepsRaisedTrue()
    {
        var signal = new GrammarBreakSignal();

        signal.Raise(MakeBreak("ProcessFirst"));
        signal.MarkObserved(MakeBreak("ProcessSecond"));

        signal.IsRaised.Should().BeTrue("Raise sticks");
        signal.ObservedCount.Should().Be(2);
    }

    [Fact]
    public void NoBreaks_BothChannelsClean()
    {
        var signal = new GrammarBreakSignal();

        signal.IsRaised.Should().BeFalse();
        signal.HasObservedBreak.Should().BeFalse();
        signal.ObservedCount.Should().Be(0);
        signal.Current.Should().BeNull();
    }
}
