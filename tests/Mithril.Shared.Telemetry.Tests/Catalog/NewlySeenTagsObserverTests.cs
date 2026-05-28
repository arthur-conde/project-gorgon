using System.Collections.Generic;
using FluentAssertions;
using Mithril.Shared.Telemetry.Catalog;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Catalog;

public class NewlySeenTagsObserverTests
{
    [Fact]
    public void Notes_unknown_keys_in_order_seen()
    {
        var o = new NewlySeenTagsObserver(capacity: 16);
        o.Note("foo"); o.Note("bar"); o.Note("foo");
        o.Snapshot().Should().Equal("foo", "bar");
    }

    [Fact]
    public void Honors_capacity_dropping_oldest()
    {
        var o = new NewlySeenTagsObserver(capacity: 2);
        o.Note("a"); o.Note("b"); o.Note("c");
        o.Snapshot().Should().Equal("b", "c");
    }

    [Fact]
    public void Raises_event_only_on_first_observation_of_a_key()
    {
        var o = new NewlySeenTagsObserver(capacity: 16);
        var seen = new List<string>();
        o.OnNewKey += k => seen.Add(k);
        o.Note("x"); o.Note("x"); o.Note("y");
        seen.Should().Equal("x", "y");
    }
}
