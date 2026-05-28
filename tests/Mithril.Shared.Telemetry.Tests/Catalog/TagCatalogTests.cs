using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Catalog;

public class TagCatalogTests
{
    private sealed class Provider(params TagDescriptor[] descriptors) : ITagDescriptorProvider
    {
        public IReadOnlyCollection<TagDescriptor> Describe() => descriptors;
    }

    [Fact]
    public void Unions_descriptors_from_all_providers()
    {
        var a = new Provider(new TagDescriptor("module.id", PiiClassification.Identifying, "Mithril.Shell", ""));
        var b = new Provider(new TagDescriptor("verb", PiiClassification.Safe, "Mithril.Arda.Player", ""));
        var catalog = new TagCatalog(new[] { a, b });
        catalog.Keys.Should().BeEquivalentTo(new[] { "module.id", "verb" });
    }

    [Fact]
    public void Conflict_on_same_key_throws_with_diagnostic()
    {
        var a = new Provider(new TagDescriptor("module.id", PiiClassification.Identifying, "A", ""));
        var b = new Provider(new TagDescriptor("module.id", PiiClassification.Sensitive, "B", ""));
        var act = () => new TagCatalog(new[] { a, b });
        act.Should().Throw<InvalidOperationException>().WithMessage("*module.id*conflicting*");
    }

    [Fact]
    public void TryGetDescriptor_returns_false_for_unknown_key()
    {
        var catalog = new TagCatalog(Array.Empty<ITagDescriptorProvider>());
        catalog.TryGetDescriptor("nope", out var d).Should().BeFalse();
        d.Should().BeNull();
    }
}
