using System.Windows;
using Mithril.GameState.Skills;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;
using Smaug.Domain;
using Smaug.Parsing;

namespace Smaug.State;

/// <summary>
/// Subscribes (via the L1 <see cref="ILogStreamDriver"/>) to the LocalPlayer
/// pipe eagerly during <c>StartAsync</c> (#695 Call 1), parses vendor-related
/// lines, and feeds recorded sells into <see cref="PriceCalibrationService"/>.
/// The Smaug module gate no longer gates state subscription; UI hydration
/// (the calibration tab) stays lazy.
///
/// <para><b>L1 migration shape (#550 PR 3, archetype-B).</b> Subscribes to
/// <see cref="LocalPlayerLogLine"/> with
/// <see cref="ReplayMode.FromSessionStart"/> — <c>NpcInteractionStarted</c> /
/// <c>VendorScreenOpened</c> context lines must arrive before any
/// <c>VendorItemSold</c> for that sell to be attributable; <c>LiveOnly</c>
/// would silently skip sells that landed in the replay window.</para>
///
/// <para><b>Civic Pride via shared skill state (#580).</b> Smaug no longer
/// re-parses <c>ProcessLoadSkills</c> / <c>ProcessUpdateSkill</c> for the
/// CivicPride skill — it consumes the shared
/// <see cref="IPlayerSkillState"/> service (#462 / PR #465) and reads the
/// effective level (<c>Level + BonusLevels</c>) off the replayed snapshot.
/// The subscription is taken before the L1 vendor subscription, and
/// <see cref="IPlayerSkillState.Subscribe"/> delivers the current snapshot
/// synchronously under its lock, so a CivicPride level known to the tracker
/// at subscribe time is set on <see cref="_context"/> before any vendor
/// envelope is dispatched. Per Class A finding #2 from the consumer audit
/// (#579) and the consumption-side rule documented in
/// <c>docs/module-charters.md</c> post-PR #578.</para>
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
/// mutations (<c>VendorScreenOpened</c>, <c>NpcInteractionStarted</c>,
/// CivicPride snapshot replay) are last-write-wins and structurally
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
    private readonly IPlayerSkillState _skillState;
    private readonly IDiagnosticsSink? _diag;
    private ILogSubscription? _subscription;
    private IDisposable? _skillSubscription;

    public VendorIngestionService(
        ILogStreamDriver driver,
        VendorLogParser parser,
        PriceCalibrationService calibration,
        VendorSellContext context,
        IActiveCharacterService activeCharacter,
        IPlayerSkillState skillState,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _calibration = calibration;
        _context = context;
        _activeCharacter = activeCharacter;
        _skillState = skillState;
        _diag = diag;
    }

    /// <summary>
    /// Eager subscription attach per Call 1 / principle eager-always (#695):
    /// the L1 LocalPlayer subscription and the shared skill-state subscribe
    /// both happen during the IHostedService chain's <c>StartAsync</c>,
    /// before the trailing world-merger drain starts (#702 / Call 2). The
    /// Smaug module gate no longer gates state subscription — gate-driven
    /// UI hydration remains Smaug's own concern (today: lazy
    /// CalibrationViewModel hydration on tab activation, which reads the
    /// PriceCalibrationService snapshot independently of this service).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Smaug",
            "Subscribing to L1 driver (LocalPlayer pipe) + shared skill state for vendor events (eager attach)");

        PrimeCivicPrideFromActiveCharacter();
        _activeCharacter.ActiveCharacterChanged += OnActiveCharacterChanged;
        _activeCharacter.CharacterExportsChanged += OnActiveCharacterChanged;

        // Subscribe to shared skill state (#580). Subscribe is atomic
        // replay + live under the tracker's lock, so the current snapshot
        // — including any CivicPride observed pre-attach — is delivered
        // synchronously before this call returns, and before any vendor
        // envelope reaches the L1 handler below.
        _skillSubscription = _skillState.Subscribe(OnSkillSnapshot);

        // Latent-bug fix per #549 + #550 capability E.
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

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
            _skillSubscription?.Dispose();
            _skillSubscription = null;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Symmetric with the StartAsync subscribe so a bootstrap failure
        // that disposes the service without ExecuteAsync ever running
        // doesn't leak the active-character handlers.
        _activeCharacter.ActiveCharacterChanged -= OnActiveCharacterChanged;
        _activeCharacter.CharacterExportsChanged -= OnActiveCharacterChanged;
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _activeCharacter.ActiveCharacterChanged -= OnActiveCharacterChanged;
        _activeCharacter.CharacterExportsChanged -= OnActiveCharacterChanged;
        _subscription?.Dispose();
        _subscription = null;
        _skillSubscription?.Dispose();
        _skillSubscription = null;
        base.Dispose();
    }

    /// <summary>
    /// Whole-snapshot handler — invoked synchronously by
    /// <see cref="IPlayerSkillState.Subscribe"/> with the current snapshot
    /// on attach (replay) and on every subsequent
    /// <c>ProcessLoadSkills</c> / <c>ProcessUpdateSkill</c>.
    ///
    /// <para>If the snapshot does not carry <c>CivicPride</c> (cold session
    /// before the first <c>ProcessLoadSkills</c> of the session, or a
    /// partial-snapshot warm-up window), <b>do not</b> reset
    /// <c>_context.CivicPrideLevel</c> — preserve whatever the prime-from-export
    /// path or a prior live snapshot already set. The shared skill snapshot is
    /// authoritative when the key is present; absent is "not yet observed",
    /// not "the skill is unlearned".</para>
    ///
    /// <para><b>Threading.</b> Per the <see cref="IPlayerSkillState.Subscribe"/>
    /// contract this runs <b>synchronously under the tracker's lock</b> — on
    /// the caller's thread for the initial replay and on the L1 ingestion
    /// thread for live dispatch — <b>not</b> on the WPF dispatcher. Safe today
    /// because <c>_context.CivicPrideLevel</c> is a plain field write on a POCO
    /// and the downstream WPF binding path
    /// (<c>PriceCalibrationService.DataChanged</c> → <c>CalibrationViewModel</c>)
    /// already hops to the dispatcher via the L1 subscription's
    /// <see cref="DeliveryContext.Marshaled"/>. <b>Future trap:</b> if
    /// <see cref="VendorSellContext"/> grows <c>INotifyPropertyChanged</c>
    /// properties bound directly to the UI, this handler must marshal to the
    /// UI thread before mutating them.</para>
    /// </summary>
    private void OnSkillSnapshot(PlayerSkillSnapshot snapshot)
    {
        if (!snapshot.Skills.TryGetValue("CivicPride", out var cp)) return;
        var effective = cp.Level + cp.BonusLevels;
        if (_context.CivicPrideLevel == effective) return;
        _context.CivicPrideLevel = effective;
        _diag?.Trace("Smaug.Skills",
            $"CivicPride level={effective} (raw={cp.Level}+bonus={cp.BonusLevels}) from IPlayerSkillState");
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e) =>
        PrimeCivicPrideFromActiveCharacter(overwrite: true);

    /// <summary>
    /// Fallback path for lazy-activated sessions: if the ProcessLoadSkills line scrolled past
    /// before Smaug's ingestion loop was running, pull Civic Pride from the character export.
    /// When the active character changes, overwrites unconditionally; on first prime, only fills in
    /// a zero level so a live log-parsed value is not clobbered by a stale export.
    ///
    /// <para><b>Ordering — live wins over export.</b> <see cref="StartAsync"/>
    /// calls this <em>before</em> subscribing to <see cref="IPlayerSkillState"/>,
    /// so the export-derived value is in place first and the subsequent
    /// synchronous replay of <see cref="OnSkillSnapshot"/> overwrites it with
    /// the live tracker snapshot. This is deliberate: the character export is a
    /// stale on-disk file (last <c>/dumpchar</c>), while <c>IPlayerSkillState</c>
    /// reflects the current session — flipping the order would let a stale export
    /// clobber correct live data. The post-prime <c>ActiveCharacterChanged</c>
    /// overwrite path is the one exception (a deliberate character switch).</para>
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
