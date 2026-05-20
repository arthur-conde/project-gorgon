using System.Windows;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Smaug.Domain;
using Smaug.Parsing;

namespace Smaug.State;

/// <summary>
/// Subscribes (via the L1 <see cref="ILogStreamDriver"/>) to the LocalPlayer
/// pipe once the Smaug module gate opens, parses vendor-related lines, and
/// feeds recorded sells into <see cref="PriceCalibrationService"/>.
///
/// <para><b>L1 migration shape (#550 PR 3, archetype-B).</b> Subscribes to
/// <see cref="LocalPlayerLogLine"/> with
/// <see cref="ReplayMode.FromSessionStart"/> — Civic-Pride (from
/// <c>ProcessLoadSkills</c>) plus <c>NpcInteractionStarted</c> /
/// <c>VendorScreenOpened</c> context lines must arrive before any
/// <c>VendorItemSold</c> for that sell to be attributable; <c>LiveOnly</c>
/// would silently skip sells that landed in the replay window.</para>
///
/// <para><b>Latent-bug fix.</b> Per <see href="https://github.com/moumantai-gg/mithril/issues/549">#549</see>
/// the pre-L1 ingestion path mutates the UI-bound
/// <c>ObservableCollection&lt;ObservationRow&gt;</c> on
/// <see cref="ViewModels.CalibrationViewModel"/> from whatever thread happens
/// to fire <c>PriceCalibrationService.DataChanged</c> — Smaug had no
/// dispatcher hop anywhere, and "worked today only by accident of
/// subscription thread affinity." This subscription uses
/// <see cref="DeliveryContext.Marshaled"/> on the WPF UI dispatcher so the
/// handler — and therefore <c>RecordObservation</c> → <c>DataChanged</c> →
/// <c>CalibrationViewModel.Refresh()</c> → <c>ObservableCollection.Clear/Add</c>
/// — runs on the dispatcher thread by construction. The cross-thread mutation
/// is now structurally impossible, not "fine in practice."</para>
///
/// <para><b>No high-water idempotence.</b> <see cref="PriceCalibrationService"/>
/// already owns per-key dedup at the sink (HashSet keyed
/// <c>SessionId|NpcKey|InternalName|PricePaid|Timestamp:O</c>); an L1
/// high-water <c>Sequence</c> filter would be redundant. The context
/// mutations (<c>CivicPrideUpdated</c>, <c>VendorScreenOpened</c>,
/// <c>NpcInteractionStarted</c>) are last-write-wins and structurally
/// idempotent under replay.</para>
///
/// <para><b>Containment.</b> The L1 driver wraps every handler invocation in
/// try/catch + rate-limited Warn (capability C of #550), retiring the
/// per-service <see cref="ThrottledWarn"/> and the try/catch that previously
/// wrapped the parse + switch body. Warnings land under <c>Smaug.Ingestion</c>
/// via the driver's <see cref="LogSubscriptionOptions.DiagnosticCategory"/>
/// override.</para>
/// </summary>
public sealed class VendorIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
    private readonly VendorLogParser _parser;
    private readonly PriceCalibrationService _calibration;
    private readonly VendorSellContext _context;
    private readonly IActiveCharacterService _activeCharacter;
    private readonly IDiagnosticsSink? _diag;
    private readonly ModuleGate _gate;
    private ILogSubscription? _subscription;

    public VendorIngestionService(
        ILogStreamDriver driver,
        VendorLogParser parser,
        PriceCalibrationService calibration,
        VendorSellContext context,
        IActiveCharacterService activeCharacter,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _calibration = calibration;
        _context = context;
        _activeCharacter = activeCharacter;
        _diag = diag;
        _gate = gates.For("smaug");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Smaug", "Waiting for module gate…");
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        _diag?.Info("Smaug", "Gate opened — subscribing to L1 driver (LocalPlayer pipe) for vendor events");

        PrimeCivicPrideFromActiveCharacter();
        _activeCharacter.ActiveCharacterChanged += OnActiveCharacterChanged;
        _activeCharacter.CharacterExportsChanged += OnActiveCharacterChanged;
        stoppingToken.Register(() =>
        {
            _activeCharacter.ActiveCharacterChanged -= OnActiveCharacterChanged;
            _activeCharacter.CharacterExportsChanged -= OnActiveCharacterChanged;
        });

        // Latent-bug fix per #549 + #550 capability E. The Smaug gate only
        // opens after the user has clicked the Smaug tab, so Application.Current
        // is guaranteed non-null and its Dispatcher already pumping by here. We
        // fall back to Inline if for any reason no Application is hosting (e.g.
        // headless integration tests that share this service) — better silent
        // best-effort than an NRE.
        var uiDispatcher = Application.Current?.Dispatcher;
        var deliveryContext = uiDispatcher is not null
            ? DeliveryContext.Marshaled(uiDispatcher)
            : DeliveryContext.Inline;

        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var raw = envelope.Payload;
                var evt = _parser.TryParse(raw.Data, raw.Timestamp.UtcDateTime);
                if (evt is null) return ValueTask.CompletedTask;

                switch (evt)
                {
                    case CivicPrideUpdated cp:
                        _context.CivicPrideLevel = cp.EffectiveLevel;
                        _diag?.Trace("Smaug.Parse",
                            $"CivicPride level={cp.EffectiveLevel} (raw={cp.Raw}+bonus={cp.Bonus})");
                        break;

                    case NpcInteractionStarted started:
                        _context.RememberEntity(started.EntityId, started.NpcKey);
                        break;

                    case VendorScreenOpened screen:
                        _context.OnVendorScreenOpened(screen.EntityId, screen.FavorTier);
                        _diag?.Trace("Smaug.Parse",
                            $"VendorScreen entity={screen.EntityId} npc={_context.ActiveNpcKey ?? "?"} tier={screen.FavorTier}");
                        break;

                    case VendorItemSold sold:
                        if (!_context.IsReadyToRecord)
                        {
                            _diag?.Trace("Smaug.Parse",
                                $"Sell of {sold.InternalName} for {sold.Price} skipped — no active vendor context");
                            break;
                        }
                        _calibration.RecordObservation(
                            _context.ActiveNpcKey!,
                            sold.InternalName,
                            sold.Price,
                            _context.ActiveFavorTier!,
                            _context.CivicPrideLevel,
                            // Use the log-line timestamp (TZ-correct via the L0 source clock,
                            // #513) so replay-on-relaunch produces the same persisted
                            // Timestamp and dedup short-circuits. UtcNow at record time
                            // would diverge.
                            raw.Timestamp);
                        break;
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = deliveryContext,
                DiagnosticCategory = "Smaug.Ingestion",
            });

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e) =>
        PrimeCivicPrideFromActiveCharacter(overwrite: true);

    /// <summary>
    /// Fallback path for lazy-activated sessions: if the ProcessLoadSkills line scrolled past
    /// before Smaug's ingestion loop was running, pull Civic Pride from the character export.
    /// When the active character changes, overwrites unconditionally; on first prime, only fills in
    /// a zero level so a live log-parsed value is not clobbered by a stale export.
    /// </summary>
    private void PrimeCivicPrideFromActiveCharacter(bool overwrite = false)
    {
        if (!overwrite && _context.CivicPrideLevel > 0) return;
        var snapshot = _activeCharacter.ActiveCharacter;
        if (snapshot is null) return;
        if (!snapshot.Skills.TryGetValue("CivicPride", out var skill)) return;
        var effective = skill.Level + skill.BonusLevels;
        if (effective <= 0) return;
        _context.CivicPrideLevel = effective;
        _diag?.Info("Smaug",
            $"Primed CivicPride from export: level={effective} (raw={skill.Level}+bonus={skill.BonusLevels})");
    }
}
