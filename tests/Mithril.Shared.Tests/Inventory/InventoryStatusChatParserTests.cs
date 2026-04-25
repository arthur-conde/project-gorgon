using FluentAssertions;
using Mithril.Shared.Inventory;
using Xunit;

namespace Mithril.Shared.Tests.Inventory;

public class InventoryStatusChatParserTests
{
    [Theory]
    [InlineData("[Status] Egg added to inventory.", "Egg", 1)]
    [InlineData("[Status] Rat Tail added to inventory.", "Rat Tail", 1)]
    [InlineData("[Status] Shoddy Phlogiston x5 added to inventory.", "Shoddy Phlogiston", 5)]
    [InlineData("[Status] Guava x42 added to inventory.", "Guava", 42)]
    [InlineData("[Status] James Eltibule's Helm of Lycanthropy added to inventory.", "James Eltibule's Helm of Lycanthropy", 1)]
    [InlineData("[Status] Decent Phlogiston x18 added to inventory.", "Decent Phlogiston", 18)]
    public void RecognisesStatusLines(string line, string expectedName, int expectedCount)
    {
        var parsed = InventoryStatusChatParser.TryParse(line);

        parsed.Should().NotBeNull();
        parsed!.Value.DisplayName.Should().Be(expectedName);
        parsed.Value.Count.Should().Be(expectedCount);
    }

    [Theory]
    [InlineData("26-04-25 15:10:48\t[Status] Shoddy Phlogiston x5 added to inventory.", "Shoddy Phlogiston", 5)]
    [InlineData("[Global] Joltknocker: wtb 1 phoenix egg 25k", null, 0)]
    [InlineData("[Trade] Kanowins: Thank you.  wtb phoenix egg 35k", null, 0)]
    [InlineData("[Status] Guild status changed.", null, 0)]
    [InlineData("", null, 0)]
    public void HandlesRealAndUnrelatedLines(string line, string? expectedName, int expectedCount)
    {
        var parsed = InventoryStatusChatParser.TryParse(line);

        if (expectedName is null)
        {
            parsed.Should().BeNull();
        }
        else
        {
            parsed.Should().NotBeNull();
            parsed!.Value.DisplayName.Should().Be(expectedName);
            parsed.Value.Count.Should().Be(expectedCount);
        }
    }

    [Fact]
    public void DoesNotMisparseCountedFormAsSingle()
    {
        // Critical: "[Status] Guava x42 added to inventory." must NOT match the
        // single-form regex with name="Guava x42". The counted form is tried first.
        var parsed = InventoryStatusChatParser.TryParse("[Status] Guava x42 added to inventory.");

        parsed.Should().NotBeNull();
        parsed!.Value.DisplayName.Should().Be("Guava");
        parsed.Value.DisplayName.Should().NotContain("x");
    }
}
