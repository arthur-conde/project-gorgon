using FluentAssertions;
using Legolas.Sharing;
using Legolas.ViewModels;

namespace Legolas.Tests.Sharing;

public class LegolasShareCardViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End   = new(2026, 5, 6, 12, 8, 30, TimeSpan.Zero);

    [Fact]
    public void Named_payload_renders_character_as_title()
    {
        var payload = new LegolasSharePayload
        {
            CharacterName = "Argothian",
            StartedAt = Start,
            CompletedAt = End,
            SurveyCount = 4,
        };

        var vm = new LegolasShareCardViewModel(payload, refData: null, iconCache: null);

        vm.CharacterTitle.Should().Be("Argothian");
        vm.HasIdentity.Should().BeTrue();
        vm.SurveyCountText.Should().Be("4 surveys");
        vm.ElapsedText.Should().Be("8m 30s");
    }

    [Fact]
    public void Anonymous_payload_falls_back_to_module_title()
    {
        var payload = new LegolasSharePayload
        {
            CharacterName = null,
            StartedAt = Start,
            CompletedAt = End,
            SurveyCount = 1,
        };

        var vm = new LegolasShareCardViewModel(payload, refData: null, iconCache: null);

        vm.CharacterTitle.Should().Be("Legolas · Survey");
        vm.HasIdentity.Should().BeFalse();
        vm.SurveyCountText.Should().Be("1 survey");
    }

    [Fact]
    public void Items_sorted_by_count_descending_then_name()
    {
        var payload = new LegolasSharePayload
        {
            StartedAt = Start,
            CompletedAt = End,
            CollectedItemsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Coal",     2 },
                { "Diamond", 12 },
                { "Apple",    5 },
                { "Banana",   5 },
            },
        };

        var vm = new LegolasShareCardViewModel(payload, refData: null, iconCache: null);

        vm.Items.Should().HaveCount(4);
        vm.Items[0].Name.Should().Be("Diamond");
        vm.Items[0].Count.Should().Be(12);
        vm.Items[0].CountText.Should().Be("×12");
        // Apple/Banana tie at 5; alphabetic order breaks the tie.
        vm.Items[1].Name.Should().Be("Apple");
        vm.Items[2].Name.Should().Be("Banana");
        vm.Items[3].Name.Should().Be("Coal");
    }

    [Fact]
    public void Items_capped_at_card_max()
    {
        var items = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 30; i++)
            items[$"Item{i:D2}"] = i + 1;
        var payload = new LegolasSharePayload
        {
            StartedAt = Start,
            CompletedAt = End,
            CollectedItemsByInternalName = items,
        };

        var vm = new LegolasShareCardViewModel(payload, refData: null, iconCache: null);
        vm.Items.Count.Should().BeLessThanOrEqualTo(12,
            "the card has finite vertical room — overflow goes to the JSON / summary surfaces");
    }

    [Fact]
    public void Without_refdata_or_icon_cache_items_have_no_icon()
    {
        var payload = new LegolasSharePayload
        {
            StartedAt = Start,
            CompletedAt = End,
            CollectedItemsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Diamond", 1 },
            },
        };
        var vm = new LegolasShareCardViewModel(payload, refData: null, iconCache: null);
        vm.Items.Single().HasIcon.Should().BeFalse();
        vm.Items.Single().Icon.Should().BeNull();
    }
}
