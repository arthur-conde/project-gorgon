namespace Gorgon.Shared.Reference;

/// <summary>
/// Fetches community-aggregated calibration rates from the gorgon-calibration repo and
/// hands them to the per-module calibration services for merging with local observations.
/// Mirrors <see cref="IReferenceDataService"/>'s conventions (file snapshot, background refresh,
/// atomic cache write, <see cref="FileUpdated"/> event) but points at a different host and schema.
/// </summary>
public interface ICommunityCalibrationService
{
    /// <summary>Current Samwise payload in memory, or null if never loaded.</summary>
    GrowthRatesPayload? SamwiseRates { get; }

    /// <summary>Current Arwen payload in memory, or null if never loaded.</summary>
    GiftRatesPayload? ArwenRates { get; }

    /// <summary>File keys this service knows about: "samwise", "arwen".</summary>
    IReadOnlyList<string> Keys { get; }

    /// <summary>Snapshot metadata for a file (source, fetched-at, entry count).</summary>
    ReferenceFileSnapshot GetSnapshot(string key);

    /// <summary>Refresh a single file from GitHub raw. Keeps existing cache on failure.</summary>
    Task RefreshAsync(string key, CancellationToken ct = default);

    /// <summary>Refresh both files.</summary>
    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Kick off <see cref="RefreshAllAsync"/> on a background task.</summary>
    void BeginBackgroundRefresh();

    /// <summary>Delete both cached files and clear in-memory payloads.</summary>
    void ClearCache();

    /// <summary>Fires with the file key after a successful refresh or cache load.</summary>
    event EventHandler<string>? FileUpdated;
}
