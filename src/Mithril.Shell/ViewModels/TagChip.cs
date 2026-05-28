using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Telemetry.Abstractions;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// A single chip in the telemetry tag cloud. <see cref="IsExported"/> mutations
/// fire <see cref="IsExportedChanged"/> so the parent VM can write through to
/// <see cref="Mithril.Shared.Telemetry.Settings.TelemetrySettings.TagExports"/>
/// — keeps the VM-to-settings mutation rule centralised on
/// <see cref="TelemetrySettingsViewModel"/>.
/// </summary>
public sealed partial class TagChip : ObservableObject
{
    public string Key { get; }
    public PiiClassification Classification { get; }
    public string Description { get; }

    private bool _isExported;
    public bool IsExported
    {
        get => _isExported;
        set
        {
            if (_isExported == value) return;
            _isExported = value;
            OnPropertyChanged();
            IsExportedChanged?.Invoke(this);
        }
    }

    /// <summary>Fired after <see cref="IsExported"/> is mutated. Consumed by
    /// <see cref="TelemetrySettingsViewModel"/> to persist the override.</summary>
    public event Action<TagChip>? IsExportedChanged;

    public TagChip(string key, PiiClassification classification, string description, bool isExported)
    {
        Key = key;
        Classification = classification;
        Description = description;
        _isExported = isExported;
    }
}
