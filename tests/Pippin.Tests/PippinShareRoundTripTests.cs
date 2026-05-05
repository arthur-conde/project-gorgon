using System.Text.Json;
using FluentAssertions;
using Mithril.Shared.Sharing;
using Pippin.Sharing;
using Xunit;

namespace Pippin.Tests;

public class PippinShareRoundTripTests
{
    [Fact]
    public void Payload_round_trips_through_codec()
    {
        var payload = new PippinSharePayload
        {
            CharacterName = "Argothian",
            EatenFoodsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["FoodApplePie"] = 5,
                ["FoodBacon"] = 12,
            },
            UnknownByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mystery Stew"] = 1,
            },
            LastReportTime = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(payload, PippinShareJsonContext.Default.PippinSharePayload);
        var encoded = ShareCodec.EncodePayload(json);
        ShareCodec.TryDecodePayload(encoded, out var decodedJson, out _).Should().BeTrue();
        var decoded = JsonSerializer.Deserialize(decodedJson, PippinShareJsonContext.Default.PippinSharePayload);

        decoded.Should().NotBeNull();
        decoded!.CharacterName.Should().Be("Argothian");
        decoded.EatenFoodsByInternalName.Should().HaveCount(2);
        decoded.EatenFoodsByInternalName["FoodApplePie"].Should().Be(5);
        decoded.EatenFoodsByInternalName["FoodBacon"].Should().Be(12);
        decoded.UnknownByName.Should().NotBeNull();
        decoded.UnknownByName!.Should().ContainKey("Mystery Stew").WhoseValue.Should().Be(1);
        decoded.LastReportTime.Should().Be(payload.LastReportTime);
    }

    [Fact]
    public void Payload_without_character_name_omits_field_from_json()
    {
        var payload = new PippinSharePayload
        {
            CharacterName = null,
            EatenFoodsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal) { ["FoodBacon"] = 1 },
        };

        var json = JsonSerializer.Serialize(payload, PippinShareJsonContext.Default.PippinSharePayload);

        json.Should().NotContain("characterName",
            "opt-out path should produce an anonymous payload with no character-name field");
    }

    [Fact]
    public void Empty_unknown_dictionary_is_omitted_from_json()
    {
        var payload = new PippinSharePayload
        {
            EatenFoodsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal) { ["FoodBacon"] = 1 },
            // UnknownByName left null
        };

        var json = JsonSerializer.Serialize(payload, PippinShareJsonContext.Default.PippinSharePayload);

        json.Should().NotContain("unknownByName");
    }

    [Fact]
    public void Realistic_completionist_payload_fits_url_budget()
    {
        var payload = new PippinSharePayload
        {
            CharacterName = "ArgothianTheLongNamed",
            LastReportTime = DateTimeOffset.UtcNow,
        };
        for (var i = 0; i < 614; i++)
            payload.EatenFoodsByInternalName[$"FoodOverlyLongInternalName{i:D3}"] = 99;

        var json = JsonSerializer.Serialize(payload, PippinShareJsonContext.Default.PippinSharePayload);
        var encoded = ShareCodec.EncodePayload(json);

        encoded.Length.Should().BeLessThan(16_384,
            "fits inside the deep-link router's pippin payload cap");
    }
}
