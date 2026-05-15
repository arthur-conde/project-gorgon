using FluentAssertions;
using Silmarillion.ViewModels;
using Xunit;
using PlayerTitlePoco = Mithril.Reference.Models.Misc.PlayerTitle;

namespace Silmarillion.Tests.ViewModels;

/// <summary>
/// PlayerTitle detail VM. No Gate-C "quests awarding this title" popup test:
/// per #248 the Quest→title linkage is unstructured (BestowTitle args live in a
/// different namespace from the Title_N envelope keys with no matching identifier
/// on the POCO), so the popup-from-index surface is intentionally not built. The
/// Gate-C test is merge-blocking only IF that popup ships; here we instead assert
/// the detail VM exposes no such surface.
/// </summary>
public sealed class PlayerTitleDetailViewModelTests
{
    private static PlayerTitleDetailViewModel Build(string envelopeKey, PlayerTitlePoco title)
    {
        // Project through the same row builder the tab VM uses so the colour-strip
        // / facet derivation under test is the real path, not a hand-rolled row.
        var refData = new FakeRefData(envelopeKey, title);
        var vm = new PlayerTitlesTabViewModel(refData);
        vm.SelectedTitle = vm.AllTitles.Single();
        return vm.DetailViewModel!;
    }

    [Fact]
    public void Header_IsColorStripped_AndFooterIsBareEnvelopeKey()
    {
        var vm = Build("Title_5018", new PlayerTitlePoco
        {
            Title = "<color=#00cc00>Warsmith</color>",
            Tooltip = "Earned by completing a quest.",
        });

        vm.DisplayName.Should().Be("Warsmith");
        // POCO carries no InternalName → footer is just the single envelope key
        // (no divergent "X / Y" pair like Lorebook).
        vm.FooterText.Should().Be("Title_5018");
    }

    [Fact]
    public void NullTooltip_RendersPlaceholder()
    {
        var vm = Build("Title_1", new PlayerTitlePoco { Title = "<color=cyan>Game Admin</color>" });

        vm.HasTooltip.Should().BeFalse();
        vm.Tooltip.Should().BeNull();
    }

    [Fact]
    public void Tooltip_PresentedWhenNonNull()
    {
        var vm = Build("Title_15001", new PlayerTitlePoco
        {
            Title = "<color=yellow>Insane</color>",
            Tooltip = "Bestowed directly by a Grand Duke.",
            Keywords = new[] { "PlayerBestowedTitle" },
        });

        vm.HasTooltip.Should().BeTrue();
        vm.Tooltip.Should().Be("Bestowed directly by a Grand Duke.");
    }

    [Fact]
    public void ScopeBadges_NoiseFilter_OnlyTrueScopesSurface()
    {
        var none = Build("Title_1", new PlayerTitlePoco { Title = "<color=cyan>Plain</color>" });
        none.AccountWide.Should().BeFalse("null/false scope is default noise — not surfaced");
        none.SoulWide.Should().BeFalse();

        var account = Build("Title_2", new PlayerTitlePoco
        {
            Title = "<color=cyan>Acct</color>", AccountWide = true,
        });
        account.AccountWide.Should().BeTrue();
        account.SoulWide.Should().BeFalse();

        var soul = Build("Title_3", new PlayerTitlePoco
        {
            Title = "<color=cyan>Soul</color>", SoulWide = true,
        });
        soul.SoulWide.Should().BeTrue();
        soul.AccountWide.Should().BeFalse();
    }

    [Fact]
    public void Obtainability_LintFamily_DrivesNotObtainableNote()
    {
        var notObtainable = Build("Title_101", new PlayerTitlePoco
        {
            Title = "<color=white>Content Creator</color>",
            Keywords = new[] { "Lint_NotObtainable" },
        });
        notObtainable.IsObtainable.Should().BeFalse();
        notObtainable.IsNotObtainable.Should().BeTrue();

        var earnable = Build("Title_5018", new PlayerTitlePoco
        {
            Title = "<color=#00cc00>Warsmith</color>", Tooltip = "Earned by completing a quest.",
        });
        earnable.IsObtainable.Should().BeTrue();
        earnable.IsNotObtainable.Should().BeFalse();
    }

    [Fact]
    public void DetailVm_HasNoQuestsAwardingTitleSurface()
    {
        // #248 — the Quest→title linkage is unstructured, so the popup-from-index
        // surface is deliberately absent. Guard against a regression that
        // re-introduces a synthesised affordance: the VM exposes no popup / count
        // / command member for it (compile-time guard via the public surface).
        var vm = Build("Title_5018", new PlayerTitlePoco
        {
            Title = "<color=#00cc00>Warsmith</color>", Tooltip = "Earned by completing a quest.",
        });

        typeof(PlayerTitleDetailViewModel).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(n =>
                n.Contains("Quest", System.StringComparison.OrdinalIgnoreCase)
                || n.Contains("Popup", System.StringComparison.OrdinalIgnoreCase));
        vm.Should().NotBeNull();
    }

    private sealed class FakeRefData : Mithril.Shared.Reference.IReferenceDataService
    {
        private readonly Dictionary<string, PlayerTitlePoco> _titles = new(StringComparer.Ordinal);
        public FakeRefData(string key, PlayerTitlePoco title) => _titles[key] = title;

        public IReadOnlyDictionary<string, PlayerTitlePoco> PlayerTitles => _titles;

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Mithril.Reference.Models.Items.Item> Items { get; } = new Dictionary<long, Mithril.Reference.Models.Items.Item>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Items.Item> ItemsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Items.Item>();
        public Mithril.Shared.Reference.ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Mithril.Reference.Models.Items.Item>());
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> Recipes { get; } = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> RecipesByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.SkillEntry> Skills { get; } = new Dictionary<string, Mithril.Shared.Reference.SkillEntry>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.XpTableEntry> XpTables { get; } = new Dictionary<string, Mithril.Shared.Reference.XpTableEntry>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.NpcEntry> Npcs { get; } = new Dictionary<string, Mithril.Shared.Reference.NpcEntry>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Npcs.Npc> NpcsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Npcs.Npc>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.AreaEntry> Areas { get; } = new Dictionary<string, Mithril.Shared.Reference.AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<Mithril.Shared.Reference.ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<Mithril.Shared.Reference.ItemSource>>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.AttributeEntry> Attributes { get; } = new Dictionary<string, Mithril.Shared.Reference.AttributeEntry>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.PowerEntry> Powers { get; } = new Dictionary<string, Mithril.Shared.Reference.PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public Mithril.Shared.Reference.ReferenceFileSnapshot GetSnapshot(string key) => new(key, Mithril.Shared.Reference.ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
