using System;
using Mithril.Shared.Settings;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapCaptureRegionProvider"/> backed by an
/// <see cref="ISettingsStore{T}"/>. The current settings snapshot is supplied at
/// construction (the composition root loads it once via <c>store.Load()</c>);
/// <see cref="Set"/> mutates it and persists synchronously.
/// </summary>
public sealed class MapCaptureRegionProvider : IMapCaptureRegionProvider
{
    private readonly ISettingsStore<MapCaptureSettings> _store;
    private readonly MapCaptureSettings _settings;

    public MapCaptureRegionProvider(ISettingsStore<MapCaptureSettings> store, MapCaptureSettings settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public CaptureRect? Current => _settings.MapBbox;

    public event EventHandler? Changed;

    public void Set(CaptureRect rect)
    {
        _settings.MapBbox = rect;
        _store.Save(_settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
