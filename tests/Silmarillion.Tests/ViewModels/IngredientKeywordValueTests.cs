using FluentAssertions;
using Mithril.Reference;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public class IngredientKeywordValueTests
{
    [Fact]
    public void QueryStringValue_returns_Tag()
    {
        var value = new IngredientKeywordValue("Crystal");

        value.QueryStringValue.Should().Be("Crystal");
    }

    [Fact]
    public void Implements_IQueryStringValue()
    {
        IQueryStringValue value = new IngredientKeywordValue("Crystal");

        value.QueryStringValue.Should().Be("Crystal");
    }
}
