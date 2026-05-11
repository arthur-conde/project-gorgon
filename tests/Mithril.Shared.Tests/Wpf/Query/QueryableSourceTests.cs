using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class QueryableSourceTests
{
    private sealed record Row(string Crop, int Samples, bool Active);

    private static readonly Row[] Dataset =
    {
        new("Red Aster",  19, true),
        new("Daisy",       3, true),
        new("Tundra Rye",  3, false),
        new("Pumpkin",    12, true),
    };

    [Fact]
    public void Empty_querytext_yields_null_predicate_and_passthrough_apply()
    {
        var qs = new QueryableSource<Row>();

        qs.Predicate.Should().BeNull();
        qs.Error.Should().BeNull();
        qs.Apply(Dataset).Should().BeEquivalentTo(Dataset);

        qs.QueryText = "   ";
        qs.Predicate.Should().BeNull();
        qs.Apply(Dataset).Should().BeEquivalentTo(Dataset);
    }

    [Fact]
    public void Valid_query_compiles_to_matching_predicate()
    {
        var qs = new QueryableSource<Row> { QueryText = "samples > 5 AND active = TRUE" };

        qs.Error.Should().BeNull();
        qs.Predicate.Should().NotBeNull();
        qs.Apply(Dataset).Select(r => r.Crop).Should().BeEquivalentTo("Red Aster", "Pumpkin");
    }

    [Fact]
    public void Malformed_query_populates_error_and_retains_last_good_predicate()
    {
        var qs = new QueryableSource<Row> { QueryText = "samples > 5" };
        var lastGood = qs.Predicate;
        lastGood.Should().NotBeNull();

        qs.QueryText = "samples > >> oops";

        qs.Error.Should().NotBeNullOrEmpty();
        qs.Predicate.Should().BeSameAs(lastGood);
        qs.Apply(Dataset).Select(r => r.Crop).Should().BeEquivalentTo("Red Aster", "Pumpkin");
    }

    [Fact]
    public void Schema_auto_built_from_T_exposes_public_properties()
    {
        var qs = new QueryableSource<Row>();

        qs.Schema.Select(c => c.Name).Should().BeEquivalentTo("Crop", "Samples", "Active");
        qs.Schema.Single(c => c.Name == "Samples").ValueType.Should().Be<int>();
        qs.Schema.Single(c => c.Name == "Crop").IsNullable.Should().BeTrue();
        qs.Schema.Single(c => c.Name == "Active").IsNullable.Should().BeFalse();
    }

    [Fact]
    public void Schema_override_via_explicit_bindings_is_respected()
    {
        // Only expose "name" — even though Row has more public properties.
        var bindings = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new("name", typeof(string), r => ((Row)r).Crop),
        };
        var qs = new QueryableSource<Row>(bindings);

        qs.Schema.Select(c => c.Name).Should().BeEquivalentTo("name");

        qs.QueryText = "name = 'Daisy'";
        qs.Error.Should().BeNull();
        qs.Apply(Dataset).Select(r => r.Crop).Should().BeEquivalentTo("Daisy");
    }

    [Fact]
    public void PredicateChanged_fires_when_querytext_changes()
    {
        var qs = new QueryableSource<Row>();
        int fires = 0;
        qs.PredicateChanged += (_, _) => fires++;

        qs.QueryText = "samples > 5";
        qs.QueryText = "samples > 5"; // unchanged — no fire
        qs.QueryText = "samples < 5"; // recompile

        fires.Should().Be(2);
    }

    [Fact]
    public void CaseSensitive_setter_recompiles()
    {
        // Column name must match property casing here ("Crop") because CaseSensitive
        // also tightens column-name lookup to Ordinal.
        var qs = new QueryableSource<Row> { QueryText = "Crop = 'daisy'" };
        qs.Apply(Dataset).Select(r => r.Crop).Should().BeEquivalentTo("Daisy");

        qs.CaseSensitive = true;
        qs.Error.Should().BeNull();
        qs.Apply(Dataset).Should().BeEmpty(); // 'Daisy' != 'daisy' under Ordinal
    }

    [Fact]
    public void PropertyChanged_fires_for_inputs_and_outputs()
    {
        var qs = new QueryableSource<Row>();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)qs).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        qs.QueryText = "samples > 5";

        changed.Should().Contain("QueryText");
        changed.Should().Contain("Predicate");
        changed.Should().NotContain("Error"); // valid query, error already null → no change
    }

    [Fact]
    public void Bare_text_input_throws_or_falls_through_per_engine_contract()
    {
        // QueryableSource does not have the bare-text fallback — that's a
        // MithrilDataGrid / QueryFilter (attached behavior) UX concern.
        // Bare text that doesn't parse as grammar is reported as an error,
        // mirroring "the user wrote something that isn't a query".
        var qs = new QueryableSource<Row> { QueryText = "totally not a query" };

        // Either errors out, or the parser interprets it some weird way.
        // The contract: caller can detect the problem via Error or empty
        // result, and the helper doesn't crash.
        qs.Error.Should().NotBeNull();
    }
}
