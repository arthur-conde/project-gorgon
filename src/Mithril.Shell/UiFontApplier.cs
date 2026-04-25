using System.ComponentModel;
using System.Windows;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;

namespace Mithril.Shell;

// Pushes ShellSettings.UiFontFamily / UiFontSize into every loaded ResourceDictionary
// that defines the font keys. Per-view <MergedDictionaries> re-import Mithril.Shared's
// Resources.xaml, so there are multiple copies of AppFontSize / AppFontFamily scattered
// across the logical tree — DynamicResource resolution stops at the first match, so we
// have to rewrite every copy, not just Application.Resources.
internal sealed class UiFontApplier : IDisposable
{
    private static readonly (string Key, double Reference)[] SizeKeys =
    {
        ("AppFontSizeXSmall",  9),
        ("AppFontSizeSmall",  10),
        ("AppFontSizeHint",   11),
        ("AppFontSize",       12),
        ("AppFontSizeMedium", 13),
        ("AppFontSizeLarge",  14),
        ("AppFontSizeXLarge", 16),
        ("AppFontSizeHeader", 18),
        ("AppFontSizeTitle",  20),
        ("AppFontSizeHero",   22),
    };

    private readonly ShellSettings _settings;
    private readonly Application _app;

    public UiFontApplier(Application app, ShellSettings settings)
    {
        _app = app;
        _settings = settings;
        _settings.PropertyChanged += OnChanged;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded));
        Apply();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellSettings.UiFontFamily) or nameof(ShellSettings.UiFontSize))
            Apply();
    }

    // Catch windows and controls whose resources haven't been rewritten yet (e.g. a dialog
    // that opens after a settings change). Cheap: only updates dictionaries that already
    // define our keys; leaves everything else alone.
    private void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Resources is { } rd)
            UpdateDictionary(rd, CurrentFamily(), CurrentScale(), visited: new HashSet<ResourceDictionary>());
    }

    private string CurrentFamily() =>
        string.IsNullOrWhiteSpace(_settings.UiFontFamily) ? "Segoe UI" : _settings.UiFontFamily;

    private double CurrentScale()
    {
        var size = _settings.UiFontSize <= 0 ? 12.0 : _settings.UiFontSize;
        return size / 12.0;
    }

    private void Apply()
    {
        var family = CurrentFamily();
        var scale = CurrentScale();
        var visited = new HashSet<ResourceDictionary>();

        UpdateDictionary(_app.Resources, family, scale, visited);

        foreach (Window w in _app.Windows)
            UpdateDictionary(w.Resources, family, scale, visited);
    }

    private static void UpdateDictionary(ResourceDictionary rd, string family, double scale, HashSet<ResourceDictionary> visited)
    {
        if (rd is null || !visited.Add(rd)) return;

        if (rd.Contains("AppFontFamily"))
            rd["AppFontFamily"] = new FontFamily(family);

        foreach (var (key, reference) in SizeKeys)
        {
            if (rd.Contains(key))
                rd[key] = Math.Round(reference * scale, 1);
        }

        foreach (var merged in rd.MergedDictionaries)
            UpdateDictionary(merged, family, scale, visited);
    }

    public void Dispose() => _settings.PropertyChanged -= OnChanged;
}
