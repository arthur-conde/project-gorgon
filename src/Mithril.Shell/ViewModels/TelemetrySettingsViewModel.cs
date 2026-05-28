using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Export;
using Mithril.Shared.Telemetry.Settings;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// View-model for the Settings -> Diagnostics -> Telemetry sub-section.
///
/// Mutation contract (see Task 12/13 of mithril#815): MUTATES the singleton
/// <see cref="TelemetrySettings"/> instance in place — dictionary entries
/// are added/removed/updated on <see cref="TelemetrySettings.Headers"/> and
/// <see cref="TelemetrySettings.TagExports"/> via the existing references,
/// followed by <see cref="TelemetrySettings.Touch"/> so
/// <c>SettingsAutoSaver&lt;T&gt;</c> persists and
/// <c>IOptionsMonitor.CurrentValue.TagExports</c> sees the change live
/// (the host's singleton-options-monitor returns this same instance).
///
/// Never reassigns the dictionary references — that would break the
/// hot-reload identity assumption.
/// </summary>
public sealed partial class TelemetrySettingsViewModel : ObservableObject, IDisposable
{
    private readonly TelemetrySettings _settings;
    private readonly TagCatalog _catalog;
    private readonly HeaderValueProtection _headerProtection;
    private readonly NewlySeenTagsObserver _newlySeen;
    private readonly ExporterHealthMonitor _health;
    private readonly IDisposable _healthSubscription;
    private readonly Action<string> _onNewKeyHandler;
    private bool _disposed;

    public TelemetrySettingsViewModel(
        TelemetrySettings settings,
        TagCatalog catalog,
        HeaderValueProtection headerProtection,
        NewlySeenTagsObserver newlySeen,
        ExporterHealthMonitor health)
    {
        _settings = settings;
        _catalog = catalog;
        _headerProtection = headerProtection;
        _newlySeen = newlySeen;
        _health = health;

        Headers = new ObservableCollection<HeaderEntry>(
            settings.Headers.Select(kvp => new HeaderEntry(kvp.Key, kvp.Value, isValueRevealed: false)));

        TagGroups = new ObservableCollection<TagChipGroup>(BuildTagGroups());
        NewlySeenChips = new ObservableCollection<NewlySeenChip>(
            _newlySeen.Snapshot().Select(k => new NewlySeenChip(k, PromoteNewlySeenCommand)));

        _onNewKeyHandler = OnNewKey;
        _newlySeen.OnNewKey += _onNewKeyHandler;

        _healthSubscription = _health.Subscribe(OnHealth);
    }

    /// <summary>The underlying singleton settings instance. Bound to by XAML for
    /// the restart-required fields (EnableOtlpExport, Endpoint, Protocol, ServiceName).</summary>
    public TelemetrySettings Settings => _settings;

    public ObservableCollection<HeaderEntry> Headers { get; }
    public ObservableCollection<TagChipGroup> TagGroups { get; }
    public ObservableCollection<NewlySeenChip> NewlySeenChips { get; }

    [ObservableProperty]
    private string _lastExportStatus = "No activity yet";

    private IEnumerable<TagChipGroup> BuildTagGroups()
    {
        return _catalog.Descriptors
            .GroupBy(d => d.Subsystem, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var chips = g
                    .OrderBy(d => d.Key, StringComparer.Ordinal)
                    .Select(d => CreateChip(d))
                    .ToList();
                return new TagChipGroup(g.Key, chips);
            });
    }

    private TagChip CreateChip(TagDescriptor descriptor)
    {
        var initial = _settings.TagExports.TryGetValue(descriptor.Key, out var v)
            ? v
            : descriptor.DefaultExported;
        var chip = new TagChip(descriptor.Key, descriptor.Classification, descriptor.Description, initial);
        chip.IsExportedChanged += OnChipExportedChanged;
        return chip;
    }

    private void OnChipExportedChanged(TagChip chip)
    {
        // Explicit-override semantics: always write through, even if the new
        // value matches the catalog default. Keeps the persisted intent clear.
        _settings.TagExports[chip.Key] = chip.IsExported;
        _settings.Touch(nameof(TelemetrySettings.TagExports));
    }

    [RelayCommand]
    private void AddHeader()
    {
        // Blank row — user fills in name/value, then SaveHeader commits.
        Headers.Add(new HeaderEntry(string.Empty, string.Empty, isValueRevealed: true));
    }

    [RelayCommand]
    private void RemoveHeader(HeaderEntry? entry)
    {
        if (entry is null) return;
        Headers.Remove(entry);
        if (!string.IsNullOrEmpty(entry.Name) && _settings.Headers.Remove(entry.Name))
        {
            _settings.Touch(nameof(TelemetrySettings.Headers));
        }
    }

    /// <summary>
    /// Commit a header row to <see cref="TelemetrySettings.Headers"/>, wrapping
    /// the value via <see cref="HeaderValueProtection.Protect"/> so plaintext
    /// API keys never reach the persisted JSON. Idempotent — calling repeatedly
    /// with the same value re-wraps (DPAPI ciphertext is non-deterministic, so
    /// the at-rest bytes will differ, but the unwrapped plaintext is stable).
    /// </summary>
    [RelayCommand]
    private void SaveHeader(HeaderEntry? entry)
    {
        if (entry is null) return;
        if (string.IsNullOrWhiteSpace(entry.Name)) return;
        var wrapped = _headerProtection.Protect(entry.Value) ?? string.Empty;
        _settings.Headers[entry.Name] = wrapped;
        _settings.Touch(nameof(TelemetrySettings.Headers));
    }

    /// <summary>
    /// Toggle reveal for a row. v1 deferral: no confirmation dialog — the
    /// "AskUserConfirm" UX polish is tracked separately. For now, revealing
    /// is a single click; the value cell flips between masked and plaintext.
    /// </summary>
    [RelayCommand]
    private static void RevealValue(HeaderEntry? entry)
    {
        if (entry is null) return;
        entry.IsValueRevealed = !entry.IsValueRevealed;
    }

    [RelayCommand]
    private void PromoteNewlySeen(NewlySeenChip? chip)
    {
        if (chip is null) return;
        _settings.TagExports[chip.Key] = true;
        _settings.Touch(nameof(TelemetrySettings.TagExports));
        NewlySeenChips.Remove(chip);
    }

    private void OnNewKey(string key)
    {
        // Already-promoted keys may resurface via the observer (the observer
        // doesn't know about user state). Skip ones already exported.
        if (_settings.TagExports.TryGetValue(key, out var exported) && exported) return;
        if (NewlySeenChips.Any(c => c.Key == key)) return;
        NewlySeenChips.Add(new NewlySeenChip(key, PromoteNewlySeenCommand));
    }

    private void OnHealth(ExporterHealth health)
    {
        LastExportStatus = Format(health);
    }

    private static string Format(ExporterHealth health)
    {
        if (health.LastSuccessUtc is null && health.LastFailureUtc is null)
        {
            return "No activity yet";
        }
        if (health.LastFailureUtc is { } f
            && (health.LastSuccessUtc is null || f > health.LastSuccessUtc))
        {
            var rel = FormatRelative(f);
            return string.IsNullOrEmpty(health.LastError)
                ? $"Last export failed {rel}"
                : $"Last export failed {rel}: {health.LastError}";
        }
        return $"Last successful export {FormatRelative(health.LastSuccessUtc!.Value)}";
    }

    private static string FormatRelative(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when;
        if (delta.TotalSeconds < 5) return "just now";
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _newlySeen.OnNewKey -= _onNewKeyHandler;
        _healthSubscription.Dispose();
        foreach (var group in TagGroups)
        {
            foreach (var chip in group.Chips)
            {
                chip.IsExportedChanged -= OnChipExportedChanged;
            }
        }
    }
}
