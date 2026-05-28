using System.Collections.Generic;
using System.ComponentModel;

namespace Mithril.Shared.Telemetry.Settings;

/// <summary>
/// Minimal stub for the telemetry settings root. Task 8 (mithril#815) replaces
/// this with the full schema (endpoint, DPAPI-wrapped headers, sampling, etc.);
/// the only surface currently consumed is <see cref="TagExports"/> by
/// <see cref="Processing.AllowlistAndRedactionProcessor"/>.
/// </summary>
public sealed class TelemetrySettings : INotifyPropertyChanged
{
    /// <summary>
    /// Per-tag-key user export overrides. When a key has an entry, the value
    /// wins over the descriptor's default; absent keys fall back to
    /// <see cref="Abstractions.TagDescriptor.DefaultExported"/>.
    /// </summary>
    public Dictionary<string, bool> TagExports { get; set; } = new();

    /// <inheritdoc />
#pragma warning disable CS0067 // event never raised - stub; Task 8 wires [ObservableProperty]
    public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
}
