using Celebrimbor.Domain;
using Celebrimbor.Services;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Celebrimbor.Tests;

public class CraftListFormatTests
{
    private static FakeReferenceData Data => new(
        [
            FakeReferenceData.Item(10, "Milk"),
            FakeReferenceData.Item(11, "Butter"),
        ],
        [
            FakeReferenceData.Recipe("Butter", "Cheesemaking", 0,
                ingredients: [new RecipeItemRef(10, 2, null)],
                results: [new RecipeItemRef(11, 1, null)]),
        ]);

    [Fact]
    public void Round_trip_preserves_entries()
    {
        var original = new List<CraftListEntry>
        {
            new() { RecipeInternalName = "Butter", Quantity = 3 },
        };
        var text = CraftListFormat.Serialize(original);

        var parsed = CraftListFormat.Parse(text, Data);

        parsed.Warnings.Should().BeEmpty();
        parsed.Entries.Should().ContainSingle(e => e.RecipeInternalName == "Butter" && e.Quantity == 3);
    }

    [Fact]
    public void Parse_ignores_blank_lines_and_comments()
    {
        var text = """
                   # Header comment

                   Butter x 2
                   # another comment
                   """;
        var parsed = CraftListFormat.Parse(text, Data);

        parsed.Warnings.Should().BeEmpty();
        parsed.Entries.Should().ContainSingle(e => e.RecipeInternalName == "Butter" && e.Quantity == 2);
    }

    [Fact]
    public void Parse_accepts_both_x_and_multiplication_separators()
    {
        var parsed = CraftListFormat.Parse("Butter × 5", Data);

        parsed.Warnings.Should().BeEmpty();
        parsed.Entries.Should().ContainSingle(e => e.Quantity == 5);
    }

    [Fact]
    public void Unknown_recipe_warns_and_skips()
    {
        var parsed = CraftListFormat.Parse("Butter x 1\nNotARecipe x 2", Data);

        parsed.Entries.Should().ContainSingle(e => e.RecipeInternalName == "Butter");
        parsed.Warnings.Should().Contain(w => w.Contains("NotARecipe"));
    }

    [Fact]
    public void Negative_or_zero_quantity_warns_and_skips()
    {
        var parsed = CraftListFormat.Parse("Butter x 0\nButter x -3\nButter x 2", Data);

        parsed.Entries.Should().ContainSingle().Which.Quantity.Should().Be(2);
        parsed.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public void MergeAppend_sums_duplicate_recipe_quantities()
    {
        var existing = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 2 } };
        var incoming = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 3 } };

        var merged = CraftListFormat.MergeAppend(existing, incoming);

        merged.Should().ContainSingle(e => e.RecipeInternalName == "Butter" && e.Quantity == 5);
    }

    [Fact]
    public void MergeAppend_keeps_distinct_recipes()
    {
        var existing = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 2 } };
        var incoming = new[] { new CraftListEntry { RecipeInternalName = "Bread", Quantity = 1 } };

        var merged = CraftListFormat.MergeAppend(existing, incoming);

        merged.Should().HaveCount(2);
    }

    // ---- Share link encoding ------------------------------------------------

    [Fact]
    public void ShareLink_round_trip_preserves_entries()
    {
        var original = new List<CraftListEntry>
        {
            new() { RecipeInternalName = "Butter", Quantity = 7 },
        };

        var payload = CraftListFormat.EncodeShareLink(original);
        var parsed = CraftListFormat.DecodeShareLink(payload, Data);

        parsed.Warnings.Should().BeEmpty();
        parsed.Entries.Should().ContainSingle(e => e.RecipeInternalName == "Butter" && e.Quantity == 7);
    }

    [Fact]
    public void ShareLink_payload_uses_only_base64url_chars()
    {
        var entries = new List<CraftListEntry>
        {
            new() { RecipeInternalName = "Butter", Quantity = 2 },
        };

        var payload = CraftListFormat.EncodeShareLink(entries);

        payload.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void ShareLink_empty_payload_returns_warning()
    {
        var parsed = CraftListFormat.DecodeShareLink("", Data);

        parsed.Entries.Should().BeEmpty();
        parsed.Warnings.Should().ContainSingle(w => w.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShareLink_bad_base64_returns_warning_not_throws()
    {
        // '!' is not a base64url character.
        var parsed = CraftListFormat.DecodeShareLink("not!valid", Data);

        parsed.Entries.Should().BeEmpty();
        parsed.Warnings.Should().ContainSingle(w => w.Contains("base64url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShareLink_wrong_version_byte_returns_warning()
    {
        // Hand-craft a payload with version byte 0x02 (unsupported) + two arbitrary bytes.
        var bytes = new byte[] { 0x02, 0xAB, 0xCD };
        var payload = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var parsed = CraftListFormat.DecodeShareLink(payload, Data);

        parsed.Entries.Should().BeEmpty();
        parsed.Warnings.Should().ContainSingle(w => w.Contains("version", StringComparison.OrdinalIgnoreCase));
    }
}
