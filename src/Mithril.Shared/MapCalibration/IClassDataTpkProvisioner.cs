using System.Threading;
using System.Threading.Tasks;

namespace Mithril.Shared.MapCalibration;

/// <summary>
/// Provisions the UABEA <c>classdata.tpk</c> (the ~283 KB Unity class-data
/// package) the out-of-process asset-extractor sidecar needs to decode PG icon
/// templates (#960). The artifact is third-party and is deliberately NOT bundled
/// in the repo/release (#921 keeps <c>src/**</c> decoder-free; the <c>.tpk</c> is
/// read only by the sidecar in <c>tools/</c>) — instead it is downloaded once, on
/// the user's explicit click, into the always-writable Mithril asset cache
/// (<c>%LocalAppData%/Mithril/assets/</c>), which survives Velopack app updates
/// (the swapped <c>current\</c> exe dir does not).
///
/// <para><b>Fail-soft:</b> any network / IO / verify failure is logged and
/// returned as <see cref="TpkProvisionStatus.Failed"/>; <see cref="EnsureAsync"/>
/// never throws. When the tpk is absent the engine threads no <c>--tpk</c> and the
/// sidecar safe-degrades exactly as it does today (no icons, calibration
/// degrades).</para>
/// </summary>
public interface IClassDataTpkProvisioner
{
    /// <summary>
    /// True iff a plausibly-valid <c>classdata.tpk</c> already exists at the
    /// canonical asset-cache path (existence + a cheap size-floor check — does NOT
    /// re-hash on every poll). Cheap enough to call from a status-binding getter.
    /// </summary>
    bool IsInstalled();

    /// <summary>
    /// Idempotent provisioning. If <see cref="IsInstalled"/>, returns
    /// <see cref="TpkProvisionStatus.AlreadyPresent"/> without touching the network.
    /// Otherwise downloads the tpk, verifies it (size floor AND SHA-256 against the
    /// pinned constant), and atomically moves it into the canonical path. On any
    /// failure the temp file is cleaned up and the canonical path is left untouched.
    /// Never throws.
    /// </summary>
    Task<TpkProvisionResult> EnsureAsync(IProgress<TpkProvisionProgress>? progress, CancellationToken ct);
}

/// <summary>Outcome of <see cref="IClassDataTpkProvisioner.EnsureAsync"/>.</summary>
public enum TpkProvisionStatus
{
    /// <summary>A valid tpk was already present; nothing was downloaded.</summary>
    AlreadyPresent,

    /// <summary>The tpk was downloaded, verified, and placed at the canonical path.</summary>
    Downloaded,

    /// <summary>Provisioning failed (network / IO / verify). No file was placed.</summary>
    Failed,
}

/// <summary>Result record: a status plus a human-readable message for the UI.</summary>
public sealed record TpkProvisionResult(TpkProvisionStatus Status, string Message)
{
    public bool Ok => Status != TpkProvisionStatus.Failed;
}

/// <summary>Coarse download-progress signal for the settings UI.</summary>
/// <param name="BytesReceived">Bytes downloaded so far.</param>
/// <param name="TotalBytes">Total bytes if the server reported Content-Length, else null.</param>
public readonly record struct TpkProvisionProgress(long BytesReceived, long? TotalBytes);
