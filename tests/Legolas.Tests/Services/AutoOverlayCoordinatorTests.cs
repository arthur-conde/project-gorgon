using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.Shared.Modules;

namespace Legolas.Tests.Services;

public class AutoOverlayCoordinatorTests
{
    private static (AutoOverlayCoordinator coordinator, SessionState session, LegolasSettings settings, SurveyFlowController flow) BuildSut()
    {
        var settings = new LegolasSettings();
        var session = new SessionState();
        var flow = new SurveyFlowController(session, settings);
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var coordinator = new AutoOverlayCoordinator(gates, settings, session, flow);
        return (coordinator, session, settings, flow);
    }

    private static void EnterListening(SurveyFlowController flow, SessionState session)
    {
        session.HasPlayerPosition = true;
        flow.ConfirmPlayerPosition();
    }

    [Fact]
    public void Initialize_no_change_when_inventory_hidden_and_idle()
    {
        var (coordinator, _, settings, _) = BuildSut();
        coordinator.Initialize();
        settings.ClickThroughInventory.Should().BeFalse();
    }

    [Fact]
    public void Inventory_visible_during_listening_flips_click_through_on()
    {
        var (coordinator, session, settings, flow) = BuildSut();
        EnterListening(flow, session);
        session.IsInventoryVisible = true;
        coordinator.Initialize();

        settings.ClickThroughInventory.Should().BeTrue();
    }

    [Fact]
    public void Inventory_visible_in_AwaitingPosition_does_not_flip()
    {
        var (coordinator, session, settings, _) = BuildSut();
        coordinator.Initialize();
        session.IsInventoryVisible = true;

        settings.ClickThroughInventory.Should().BeFalse(
            "no anchored session yet — user may want to interact with inventory normally");
    }

    [Fact]
    public void Transition_into_active_state_with_inventory_visible_flips_click_through()
    {
        var (coordinator, session, settings, flow) = BuildSut();
        coordinator.Initialize();
        session.IsInventoryVisible = true;
        settings.ClickThroughInventory.Should().BeFalse(); // still AwaitingPosition

        EnterListening(flow, session);

        settings.ClickThroughInventory.Should().BeTrue();
    }

    [Fact]
    public void Auto_setting_disabled_skips_flip()
    {
        var (coordinator, session, settings, flow) = BuildSut();
        settings.AutoClickThroughInventoryDuringSession = false;
        coordinator.Initialize();
        session.IsInventoryVisible = true;
        EnterListening(flow, session);

        settings.ClickThroughInventory.Should().BeFalse();
    }

    [Fact]
    public void Coordinator_does_not_force_off_when_user_disables_mid_session()
    {
        var (coordinator, session, settings, flow) = BuildSut();
        EnterListening(flow, session);
        session.IsInventoryVisible = true;
        coordinator.Initialize();
        settings.ClickThroughInventory.Should().BeTrue();

        // User flips it off manually — coordinator must not immediately re-enable
        // until a new transition or visibility change occurs.
        settings.ClickThroughInventory = false;
        settings.ClickThroughInventory.Should().BeFalse();
    }

    [Fact]
    public void Coordinator_re_enables_on_next_state_transition()
    {
        var (coordinator, session, settings, flow) = BuildSut();
        EnterListening(flow, session);
        // Add a pin to advance Ready → Listening (OptimizeRoute requires Listening).
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), gridIndex: 0)));
        session.IsInventoryVisible = true;
        coordinator.Initialize();

        settings.ClickThroughInventory = false; // user opt-out

        // Next transition — controller goes Listening → Gathering — should re-flip.
        flow.OptimizeRoute();

        settings.ClickThroughInventory.Should().BeTrue();
    }

    [Fact]
    public void Teardown_unsubscribes_and_stops_reacting()
    {
        var (coordinator, session, settings, flow) = BuildSut();
        coordinator.Initialize();
        EnterListening(flow, session);
        coordinator.Teardown();

        session.IsInventoryVisible = true; // would normally trigger
        settings.ClickThroughInventory.Should().BeFalse();
    }
}
