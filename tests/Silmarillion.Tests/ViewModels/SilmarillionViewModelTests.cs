using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class SilmarillionViewModelTests
{
    [Fact]
    public void OnNavigated_ItemKind_SwitchesTabAndCallsTarget()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var recipesTarget = new RecordingTarget(EntityKind.Recipe, tabIndex: 1);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget, recipesTarget });

        nav.Open(EntityRef.Item("Tomato"));

        vm.SelectedTabIndex.Should().Be(0);
        itemsTarget.LastSelectedInternalName.Should().Be("Tomato");
        recipesTarget.LastSelectedInternalName.Should().BeNull();
    }

    [Fact]
    public void OnNavigated_RecipeKind_SwitchesTabAndCallsTarget()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var recipesTarget = new RecordingTarget(EntityKind.Recipe, tabIndex: 1);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget, recipesTarget });

        nav.Open(EntityRef.Recipe("MakeSalsa"));

        vm.SelectedTabIndex.Should().Be(1);
        recipesTarget.LastSelectedInternalName.Should().Be("MakeSalsa");
    }

    [Fact]
    public void OnNavigated_UnregisteredKind_DoesNotChangeTab()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget });

        vm.SelectedTabIndex = 0;
        nav.Open(new EntityRef(EntityKind.Quest, "FutureQuest"));

        vm.SelectedTabIndex.Should().Be(0);  // unchanged
        itemsTarget.LastSelectedInternalName.Should().BeNull();
    }

    [Fact]
    public void OnNavigated_NpcKind_SwitchesTabAndCallsTarget()
    {
        // NPCs tab ships at index 2 (#241) — opening an Npc EntityRef should now switch the
        // tab strip and dispatch the select call to the NPCs target rather than silently
        // landing on the Items tab.
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var recipesTarget = new RecordingTarget(EntityKind.Recipe, tabIndex: 1);
        var npcsTarget = new RecordingTarget(EntityKind.Npc, tabIndex: 2);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget, recipesTarget, npcsTarget });

        nav.Open(EntityRef.Npc("NPC_Marna"));

        vm.SelectedTabIndex.Should().Be(2);
        npcsTarget.LastSelectedInternalName.Should().Be("NPC_Marna");
        itemsTarget.LastSelectedInternalName.Should().BeNull();
        recipesTarget.LastSelectedInternalName.Should().BeNull();
    }

    [Fact]
    public void OpenInWindow_NoCurrent_NoOp()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var (vm, _) = BuildVm(new IReferenceKindTarget[] { itemsTarget });

        // CanExecute should be false; explicit call is a no-op (TryOpenInWindow not invoked).
        vm.OpenInWindowCommand.CanExecute(null).Should().BeFalse();
        itemsTarget.OpenInWindowCallCount.Should().Be(0);
    }

    [Fact]
    public void OpenInWindow_CurrentItem_CallsTarget()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget });

        nav.Open(EntityRef.Item("Tomato"));
        vm.OpenInWindowCommand.Execute(null);

        itemsTarget.OpenInWindowCallCount.Should().Be(1);
    }

    [Fact]
    public void Constructor_DuplicateKind_Throws()
    {
        var targets = new IReferenceKindTarget[]
        {
            new RecordingTarget(EntityKind.Item, tabIndex: 0),
            new RecordingTarget(EntityKind.Item, tabIndex: 0),
        };
        var nav = new SilmarillionReferenceNavigator(new IReferenceKindTarget[]
        {
            new RecordingTarget(EntityKind.Recipe, tabIndex: 1),
        });

        var act = () => new SilmarillionViewModel(items: null!, recipes: null!, npcs: null!, nav, targets);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Duplicate IReferenceKindTarget*Item*");
    }

    private static (SilmarillionViewModel Vm, SilmarillionReferenceNavigator Nav) BuildVm(
        IReferenceKindTarget[] targets)
    {
        var nav = new SilmarillionReferenceNavigator(targets);
        // Empty/null tab VMs are fine — the tests only exercise the dispatch path,
        // not the tab VMs themselves (those are tested separately).
        // Pass null-forgiving stubs; SilmarillionViewModel never reads .Items / .Recipes / .Npcs here.
        var vm = new SilmarillionViewModel(items: null!, recipes: null!, npcs: null!, nav, targets);
        return (vm, nav);
    }

    private sealed class RecordingTarget : IReferenceKindTarget
    {
        public RecordingTarget(EntityKind kind, int tabIndex) { Kind = kind; TabIndex = tabIndex; }
        public EntityKind Kind { get; }
        public int TabIndex { get; }
        public string? LastSelectedInternalName { get; private set; }
        public int OpenInWindowCallCount { get; private set; }
        public bool TrySelectByInternalName(string internalName)
        {
            LastSelectedInternalName = internalName;
            return true;
        }
        public bool TryOpenInWindow()
        {
            OpenInWindowCallCount++;
            return true;
        }
    }
}
