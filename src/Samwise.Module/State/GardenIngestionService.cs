using System.Windows;
using Arda.Contracts;
using Arda.World.Player.Events;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samwise.Alarms;
using Samwise.Calibration;
using Samwise.Parsing;

namespace Samwise.State;

/// <summary>
/// Bridges Arda domain events into the <see cref="GardenStateMachine"/>.
/// Subscribes to individual typed events via <see cref="IDomainEventSubscriber"/>
/// replacing the former L1 driver + GardenLogParser + IPlayerWorld.Bus +
/// IPlayerSkillState channels.
///
/// <list type="bullet">
///   <item>Seven Player.log-sourced events (SetPetOwner, AppearanceLoop,
///   UpdateDescription, StartInteraction, UpdateItemCode, ScreenTextError,
///   PlantingCap) arrive directly as Arda domain events.</item>
///   <item>Inventory add/remove arrive as <see cref="InventoryItemAdded"/>
///   and <see cref="InventoryItemRemoved"/>.</item>
///   <item>Gardening XP arrives as <see cref="SkillUpdated"/> filtered to
///   the Gardening skill key.</item>
/// </list>
///
/// <para><b>Replay gating.</b> Arda events carry
/// <see cref="Arda.Abstractions.Logs.LogLineMetadata.IsReplay"/> which replaces
/// the legacy <c>IWorldClock.Mode</c> check and the high-water sequence
/// mechanism. The state machine's HydrateCharacter + Apply pattern handles
/// idempotent replay.</para>
///
/// <para><b>Threading.</b> Arda fires synchronously on the driver thread.
/// All state mutations marshal to the UI thread via
/// <see cref="DispatchInventory"/> / <see cref="DispatchHydrate"/> so
/// PlotChanged → bound ObservableCollection stays single-threaded.</para>
/// </summary>
public sealed class GardenIngestionService : BackgroundService
{
    private const string GardeningSkillKey = "Gardening";

    private readonly IDomainEventSubscriber _bus;
    private readonly GardenStateMachine _state;
    private readonly GardenStateService _stateService;
    private readonly ILogger? _logger;

    private IDisposable? _setPetOwnerSub;
    private IDisposable? _appearanceSub;
    private IDisposable? _updateDescSub;
    private IDisposable? _interactionSub;
    private IDisposable? _updateItemSub;
    private IDisposable? _screenTextSub;
    private IDisposable? _plantingCapSub;
    private IDisposable? _invAddedSub;
    private IDisposable? _invRemovedSub;
    private IDisposable? _skillSub;

    public GardenIngestionService(
        IDomainEventSubscriber bus,
        GardenStateMachine state,
        GardenStateService stateService,
        AlarmService alarms,
        GrowthCalibrationService calibration,
        SettingsAutoSaver<SamwiseSettings> autoSaver,
        ILogger? logger = null)
    {
        _bus = bus;
        _state = state;
        _stateService = stateService;
        _logger = logger;
        _ = alarms;       // subscribes to state.PlotChanged in ctor
        _ = calibration;  // subscribes to state.PlotChanged in ctor
        _ = autoSaver;    // subscribes to SamwiseSettings.PropertyChanged in ctor

        if (logger is not null)
        {
            state.PlotChanged += (_, e) => logger.LogTrace(
                "{CharName}/{PlotId} {OldStage} → {NewStage} ({CropType})",
                e.Plot.CharName,
                e.Plot.PlotId,
                e.OldStage?.ToString() ?? "-",
                e.NewStage,
                e.Plot.CropType ?? "?");
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Loading persisted state and subscribing to Arda domain bus (eager attach)");

        try
        {
            var (loaded, _) = await _stateService.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            DispatchHydrate(() =>
            {
                foreach (var (charName, plots) in loaded)
                    _state.HydrateCharacter(charName, plots);
            });
            _logger?.LogInformation("Hydrated {Count} character(s)", loaded.Count);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to load state"); }

        _setPetOwnerSub = _bus.Subscribe<SetPetOwnerFrame>(OnSetPetOwner);
        _appearanceSub = _bus.Subscribe<AppearanceLoopFrame>(OnAppearanceLoop);
        _updateDescSub = _bus.Subscribe<UpdateDescriptionFrame>(OnUpdateDescription);
        _interactionSub = _bus.Subscribe<InteractionStarted>(OnStartInteraction);
        _updateItemSub = _bus.Subscribe<InventoryItemUpdated>(OnUpdateItemCode);
        _screenTextSub = _bus.Subscribe<ScreenTextErrorFrame>(OnScreenTextError);
        _plantingCapSub = _bus.Subscribe<PlantingCapFrame>(OnPlantingCap);
        _invAddedSub = _bus.Subscribe<InventoryItemAdded>(OnInventoryAdded);
        _invRemovedSub = _bus.Subscribe<InventoryItemRemoved>(OnInventoryRemoved);
        _skillSub = _bus.Subscribe<SkillUpdated>(OnSkillUpdated);

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _setPetOwnerSub?.Dispose();
            _appearanceSub?.Dispose();
            _updateDescSub?.Dispose();
            _interactionSub?.Dispose();
            _updateItemSub?.Dispose();
            _screenTextSub?.Dispose();
            _plantingCapSub?.Dispose();
            _invAddedSub?.Dispose();
            _invRemovedSub?.Dispose();
            _skillSub?.Dispose();
        }
    }

    private void OnSetPetOwner(SetPetOwnerFrame evt)
    {
        var ge = new SetPetOwner(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnAppearanceLoop(AppearanceLoopFrame evt)
    {
        var ge = new AppearanceLoop(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.ModelName.ToString(),
            evt.Scale);
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnUpdateDescription(UpdateDescriptionFrame evt)
    {
        var ge = new UpdateDescription(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.PlotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            evt.Title.ToString(),
            evt.Description.ToString(),
            evt.Action.ToString(),
            evt.Scale);
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnStartInteraction(InteractionStarted evt)
    {
        var ge = new StartInteraction(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            evt.Name);
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnUpdateItemCode(InventoryItemUpdated evt)
    {
        var ge = new UpdateItemCode(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnScreenTextError(ScreenTextErrorFrame evt)
    {
        var ge = new ScreenTextError(evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow);
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnPlantingCap(PlantingCapFrame evt)
    {
        var ge = new PlantingCapReached(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.SeedDisplayName.ToString());
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnInventoryAdded(InventoryItemAdded evt)
    {
        var ge = new AddItem(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            evt.InternalName);
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnInventoryRemoved(InventoryItemRemoved evt)
    {
        var ge = new DeleteItem(
            evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            evt.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    /// <summary>
    /// Filters <see cref="SkillUpdated"/> to Gardening-only deltas and projects
    /// them into <see cref="GardeningXp"/> events for the state machine's
    /// harvest-confirmation path.
    /// </summary>
    internal void OnSkillUpdated(SkillUpdated evt)
    {
        var ge = TryProjectGardeningXp(evt);
        if (ge is null) return;

        _logger?.LogTrace(Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    /// <summary>
    /// Pure decision: does this <see cref="SkillUpdated"/> count as a
    /// gardening-XP harvest-confirmation tick? Internal for direct unit
    /// testing — the dispatch wrapper above adds only the diagnostics
    /// + UI-thread hop on top of this filter.
    /// </summary>
    internal static GardeningXp? TryProjectGardeningXp(SkillUpdated evt)
    {
        if (!string.Equals(evt.SkillKey, GardeningSkillKey, StringComparison.Ordinal)) return null;
        return new GardeningXp(evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow);
    }

    private static string Describe(GardenEvent e) => e switch
    {
        SetPetOwner spo => $"SetPetOwner  entity={spo.EntityId}",
        AppearanceLoop al => $"Appearance   model={al.ModelName}  scale={al.Scale:0.###}",
        UpdateDescription ud => $"UpdateDesc   plot={ud.PlotId}  title={ud.Title}  action={ud.Action}  scale={ud.Scale:0.###}",
        StartInteraction si => $"StartInter   plot={si.PlotId}  target={si.Target}",
        AddItem ai => $"AddItem      id={ai.ItemId}  name={ai.ItemName}",
        DeleteItem di => $"DeleteItem   id={di.ItemId}",
        UpdateItemCode uic => $"UpdateItem   id={uic.ItemId}",
        GardeningXp => "GardeningXp",
        ScreenTextError => "ScreenError",
        PlantingCapReached pcr => $"PlantingCap  seed={pcr.SeedDisplayName}",
        _ => e.GetType().Name,
    };

    private static void DispatchHydrate(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }

    private static void DispatchInventory(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
