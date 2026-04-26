using System.ComponentModel;
using FluentAssertions;
using Mithril.Shared.Modules;
using Xunit;

namespace Mithril.Shared.Tests.Modules;

public sealed class AttentionAggregatorTests
{
    private static readonly Action<Action> Inline = a => a();

    [Fact]
    public void TotalCount_SumsAcrossSources()
    {
        var a = new FakeSource("arwen", "Arwen", 2);
        var b = new FakeSource("samwise", "Samwise", 3);
        using var agg = new AttentionAggregator(new IAttentionSource[] { a, b }, Inline);

        agg.TotalCount.Should().Be(5);
        agg.HasAttention.Should().BeTrue();
        agg.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void TotalCount_IsZero_WithNoSources()
    {
        using var agg = new AttentionAggregator(Array.Empty<IAttentionSource>(), Inline);

        agg.TotalCount.Should().Be(0);
        agg.HasAttention.Should().BeFalse();
        agg.Entries.Should().BeEmpty();
    }

    [Fact]
    public void SourceChange_UpdatesTotal_AndRaisesAttentionChanged()
    {
        var a = new FakeSource("arwen", "Arwen", 0);
        using var agg = new AttentionAggregator(new[] { a }, Inline);

        var attentionEvents = new List<AttentionChangedEventArgs>();
        agg.AttentionChanged += (_, e) => attentionEvents.Add(e);

        a.Set(3);

        agg.TotalCount.Should().Be(3);
        attentionEvents.Should().ContainSingle();
        attentionEvents[0].ModuleId.Should().Be("arwen");
        attentionEvents[0].Count.Should().Be(3);
    }

    [Fact]
    public void SourceChange_RaisesPropertyChanged_ForTotalCountAndEntries()
    {
        var a = new FakeSource("arwen", "Arwen", 0);
        using var agg = new AttentionAggregator(new[] { a }, Inline);

        var props = new List<string?>();
        agg.PropertyChanged += (_, e) => props.Add(e.PropertyName);

        a.Set(1);

        props.Should().Contain(nameof(IAttentionAggregator.TotalCount));
        props.Should().Contain(nameof(IAttentionAggregator.Entries));
    }

    [Fact]
    public void HasAttention_FlipsAtZeroAndBack()
    {
        var a = new FakeSource("arwen", "Arwen", 0);
        using var agg = new AttentionAggregator(new[] { a }, Inline);

        var hasAttentionChanges = new List<bool>();
        agg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAttentionAggregator.HasAttention))
                hasAttentionChanges.Add(agg.HasAttention);
        };

        a.Set(3);
        a.Set(5); // still > 0; HasAttention shouldn't fire again
        a.Set(0);

        hasAttentionChanges.Should().Equal(true, false);
    }

    [Fact]
    public void Dispatch_IsInvoked_ForEachChange()
    {
        var dispatchedFromThread = new List<int>();
        Action<Action> capture = a =>
        {
            dispatchedFromThread.Add(Environment.CurrentManagedThreadId);
            a();
        };
        var src = new FakeSource("arwen", "Arwen", 0);
        using var agg = new AttentionAggregator(new[] { src }, capture);

        src.Set(1);
        src.Set(2);

        dispatchedFromThread.Should().HaveCount(2);
    }

    [Fact]
    public void CountFor_UnknownModule_ReturnsZero()
    {
        var src = new FakeSource("arwen", "Arwen", 4);
        using var agg = new AttentionAggregator(new[] { src }, Inline);

        agg.CountFor("samwise").Should().Be(0);
        agg.CountFor("arwen").Should().Be(4);
    }

    [Fact]
    public void Entries_ReflectsCurrentState_AfterChange()
    {
        var a = new FakeSource("arwen", "Arwen", 1);
        var b = new FakeSource("samwise", "Samwise", 2);
        using var agg = new AttentionAggregator(new IAttentionSource[] { a, b }, Inline);

        b.Set(7);

        var snapshot = agg.Entries;
        snapshot.Should().HaveCount(2);
        snapshot.Single(e => e.ModuleId == "arwen").Count.Should().Be(1);
        snapshot.Single(e => e.ModuleId == "samwise").Count.Should().Be(7);
    }

    [Fact]
    public void Dispose_UnsubscribesFromSources()
    {
        var src = new FakeSource("arwen", "Arwen", 0);
        var agg = new AttentionAggregator(new[] { src }, Inline);
        var attentionEvents = 0;
        agg.AttentionChanged += (_, _) => attentionEvents++;

        agg.Dispose();
        src.Set(5);

        attentionEvents.Should().Be(0, because: "Dispose detaches Changed handlers");
    }

    [Fact]
    public void Constructor_RejectsNullSources()
    {
        var act = () => new AttentionAggregator(sources: null!, Inline);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_AcceptsNullDispatch_AndUsesInline()
    {
        var src = new FakeSource("arwen", "Arwen", 0);
        using var agg = new AttentionAggregator(new[] { src }, dispatch: null);

        var fired = false;
        agg.AttentionChanged += (_, _) => fired = true;
        src.Set(2);

        fired.Should().BeTrue();
        agg.TotalCount.Should().Be(2);
    }

    private sealed class FakeSource : IAttentionSource
    {
        public FakeSource(string id, string label, int initial)
        {
            ModuleId = id; DisplayLabel = label; Count = initial;
        }

        public string ModuleId { get; }
        public string DisplayLabel { get; }
        public int Count { get; private set; }

        public event EventHandler? Changed;

        public void Set(int newCount)
        {
            Count = newCount;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
