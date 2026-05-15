using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Mithril.Shared.Wpf.Sorting;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Sorting;

public class ChipStateTests
{
    private sealed record Row(string Name);

    [Fact]
    public void Project_marks_active_keys_with_index_and_direction()
    {
        var available = new[]
        {
            new SortKey<Row>("Name", "Name"),
            new SortKey<Row>("Other", "Other"),
        };
        var parsedOrder = new[] { new OrderSpec("Name", OrderDirection.Descending) };

        var states = ChipState.Project(available, parsedOrder);

        states.Should().HaveCount(2);
        states[0].Key.Id.Should().Be("Name");
        states[0].IsActive.Should().BeTrue();
        states[0].Direction.Should().Be(OrderDirection.Descending);
        states[0].OrderIndex.Should().Be(0);

        states[1].Key.Id.Should().Be("Other");
        states[1].IsActive.Should().BeFalse();
        states[1].Direction.Should().BeNull();
        states[1].OrderIndex.Should().Be(-1);
    }

    [Fact]
    public void Project_matches_keys_case_insensitively()
    {
        var available = new[] { new SortKey<Row>("Name", "Name") };
        var parsed = new[] { new OrderSpec("name", OrderDirection.Ascending) };
        ChipState.Project(available, parsed)[0].IsActive.Should().BeTrue();
    }
}
