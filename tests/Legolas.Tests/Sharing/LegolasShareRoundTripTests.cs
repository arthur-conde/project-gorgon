using System.Text.Json;
using FluentAssertions;
using Legolas.Sharing;
using Legolas.ViewModels;
using Mithril.Shared.Sharing;

namespace Legolas.Tests.Sharing;

public class LegolasShareRoundTripTests
{
    [Fact]
    public void Payload_round_trips_through_codec()
    {
        var original = new LegolasSharePayload
        {
            CharacterName = "Argothian",
            StartedAt = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 5, 6, 12, 8, 30, TimeSpan.Zero),
            Mode = SessionMode.Survey,
            SurveyCount = 3,
            CollectedItemsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "RawGem_Diamond", 3 },
                { "Coal",            7 },
                { "RawOre_Iron",     2 },
            },
            UnknownByName = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Mystery Lump", 1 },
            },
        };

        var json = JsonSerializer.Serialize(original, LegolasShareJsonContext.Default.LegolasSharePayload);
        var encoded = ShareCodec.EncodePayload(json);
        ShareCodec.TryDecodePayload(encoded, out var decoded, out var err).Should().BeTrue(err);

        var roundtripped = JsonSerializer.Deserialize(decoded, LegolasShareJsonContext.Default.LegolasSharePayload);
        roundtripped.Should().NotBeNull();
        roundtripped!.CharacterName.Should().Be("Argothian");
        roundtripped.StartedAt.Should().Be(original.StartedAt);
        roundtripped.CompletedAt.Should().Be(original.CompletedAt);
        roundtripped.SurveyCount.Should().Be(3);
        roundtripped.CollectedItemsByInternalName.Should().BeEquivalentTo(original.CollectedItemsByInternalName);
        roundtripped.UnknownByName.Should().BeEquivalentTo(original.UnknownByName);
    }

    [Fact]
    public void Payload_without_character_name_omits_field_from_json()
    {
        var payload = new LegolasSharePayload
        {
            CharacterName = null,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            SurveyCount = 0,
        };
        var json = JsonSerializer.Serialize(payload, LegolasShareJsonContext.Default.LegolasSharePayload);
        json.Should().NotContain("characterName");
    }

    [Fact]
    public void Realistic_run_payload_fits_router_length_cap()
    {
        // Worst-case shape: many distinct items + a long character name.
        var items = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 30; i++)
            items[$"SurveyItemNumber{i,2:D2}"] = i + 1;

        var payload = new LegolasSharePayload
        {
            CharacterName = "AReasonablyLongCharacterName",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(45),
            Mode = SessionMode.Survey,
            SurveyCount = 30,
            CollectedItemsByInternalName = items,
        };

        var json = JsonSerializer.Serialize(payload, LegolasShareJsonContext.Default.LegolasSharePayload);
        var encoded = ShareCodec.EncodePayload(json);
        // DeepLinkRouter caps legolas payloads at 8192 base64url chars.
        encoded.Length.Should().BeLessThan(8192);
    }
}
