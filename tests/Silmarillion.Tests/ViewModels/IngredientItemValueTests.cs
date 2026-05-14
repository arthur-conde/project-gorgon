using FluentAssertions;
using Mithril.Reference;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public class IngredientItemValueTests
{
    [Fact]
    public void QueryStringValue_returns_InternalName()
    {
        var value = new IngredientItemValue("MetalSlab1");

        value.QueryStringValue.Should().Be("MetalSlab1");
    }

    [Fact]
    public void Implements_IQueryStringValue()
    {
        IQueryStringValue value = new IngredientItemValue("MetalSlab1");

        value.QueryStringValue.Should().Be("MetalSlab1");
    }
}
