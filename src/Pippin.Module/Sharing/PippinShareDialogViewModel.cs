using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Mithril.Shared.Icons;
using Mithril.Shared.Sharing;
using Mithril.Shared.Wpf.Dialogs;
using Pippin.Domain;

namespace Pippin.Sharing;

/// <summary>
/// Dialog for sharing the active character's Gourmand progress. Surfaces five outputs
/// that target disjoint audiences:
/// <list type="bullet">
///   <item><description><b>Share link</b> — <c>mithril://pippin/&lt;base64url&gt;</c>.
///         Click-to-import for other Mithril users; opens a read-only progress view in
///         the recipient's Pippin tab.</description></item>
///   <item><description><b>JSON</b> — the raw <see cref="PippinSharePayload"/> for paste
///         into bug reports, archival, or non-Mithril tooling.</description></item>
///   <item><description><b>Summary</b> — human-readable Discord/forum prose, generated
///         from the sender's catalog snapshot.</description></item>
///   <item><description><b>Card image</b> — 1000×400 social PNG. Defaults to clipboard
///         (paste straight into Discord); save-to-file as secondary.</description></item>
///   <item><description><b>Full grid image</b> — tall archival PNG. Defaults to file
///         save; clipboard as secondary.</description></item>
/// </list>
/// "Include character name" toggles whether the sender's name is embedded; defaults on
/// per the share-progress design call. Off = anonymous payload labeled "Shared progress".
///
/// When the icon cache is missing entries for the catalog, an inline preload banner
/// offers <i>Load icons now</i> instead of warning passively. Card/grid images render
/// with whatever icons the cache holds at click-time — placeholders for the rest.
/// </summary>
public sealed partial class PippinShareDialogViewModel : DialogViewModelBase
{
    private readonly Func<bool, PippinSharePayload> _buildPayload;
    private readonly Func<PippinSharePayload, string> _buildSummary;
    private readonly Func<int> _getGourmandLevel;
    private readonly PippinShareCardRenderer _renderer;
    private readonly IIconCacheService _iconCache;
    private readonly IReadOnlyList<int> _catalogIconIds;
    private readonly string? _activeCharacterName;

    public PippinShareDialogViewModel(
        Func<bool, PippinSharePayload> buildPayload,
        Func<PippinSharePayload, string> buildSummary,
        Func<int> getGourmandLevel,
        PippinShareCardRenderer renderer,
        IIconCacheService iconCache,
        IReadOnlyList<int> catalogIconIds,
        string? activeCharacterName)
    {
        _buildPayload = buildPayload;
        _buildSummary = buildSummary;
        _getGourmandLevel = getGourmandLevel;
        _renderer = renderer;
        _iconCache = iconCache;
        _catalogIconIds = catalogIconIds;
        _activeCharacterName = activeCharacterName;
        _includeCharacterName = !string.IsNullOrEmpty(activeCharacterName);
        Refresh();
        RefreshUncachedIconCount();
    }

    public override string Title => "Share Pippin Progress";
    public override string PrimaryButtonText => "Close";
    public override string? SecondaryButtonText => null;

    public bool HasCharacterName => !string.IsNullOrEmpty(_activeCharacterName);

    [ObservableProperty]
    private bool _includeCharacterName;

    [ObservableProperty]
    private string _shareLink = "";

    [ObservableProperty]
    private string _jsonPayload = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private int _uncachedIconCount;

    [ObservableProperty]
    private bool _isPrefetching;

    [ObservableProperty]
    private bool _isRendering;

    [ObservableProperty]
    private double _prefetchProgress;

    [ObservableProperty]
    private string _prefetchProgressLabel = "";

    partial void OnIncludeCharacterNameChanged(bool value) => Refresh();

    private void Refresh()
    {
        var payload = _buildPayload(IncludeCharacterName && HasCharacterName);
        JsonPayload = JsonSerializer.Serialize(payload, PippinShareJsonContext.Default.PippinSharePayload);
        ShareLink = "mithril://pippin/" + ShareCodec.EncodePayload(JsonPayload);
        Summary = _buildSummary(payload);
    }

    private void RefreshUncachedIconCount()
        => UncachedIconCount = _iconCache.GetUncachedIcons(_catalogIconIds).Count;

    [RelayCommand] private void CopyLink() => CopyText(ShareLink, "Share link copied to clipboard.");
    [RelayCommand] private void CopyJson() => CopyText(JsonPayload, "JSON copied to clipboard.");
    [RelayCommand] private void CopySummary() => CopyText(Summary, "Summary copied to clipboard.");

    [RelayCommand]
    private void SaveJson()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Pippin Progress",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = $"pippin-progress-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonPayload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusMessage = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadIconsAsync()
    {
        if (IsPrefetching) return;
        IsPrefetching = true;
        PrefetchProgress = 0;
        PrefetchProgressLabel = "";
        StatusMessage = "";

        var progress = new Progress<(int completed, int total)>(p =>
        {
            PrefetchProgress = p.total > 0 ? (double)p.completed / p.total : 1.0;
            PrefetchProgressLabel = $"{p.completed} / {p.total}";
        });

        try
        {
            await _iconCache.PreloadAsync(_catalogIconIds, progress).ConfigureAwait(true);
            RefreshUncachedIconCount();
            StatusMessage = UncachedIconCount == 0
                ? "Icons downloaded."
                : $"Downloaded what we could; {UncachedIconCount} still unavailable.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Icon download failed: {ex.Message}";
        }
        finally
        {
            IsPrefetching = false;
        }
    }

    [RelayCommand] private Task CopyCardAsync()     => RenderThen(BuildCardAsync,     image => CopyImage(image, "Card image copied to clipboard."));
    [RelayCommand] private Task SaveCardAsync()     => RenderThen(BuildCardAsync,     image => SaveImage(image, "card"));
    [RelayCommand] private Task CopyFullGridAsync() => RenderThen(BuildFullGridAsync, image => CopyImage(image, "Full grid copied to clipboard."));
    [RelayCommand] private Task SaveFullGridAsync() => RenderThen(BuildFullGridAsync, image => SaveImage(image, "full"));

    private Task<BitmapSource> BuildCardAsync()
    {
        var payload = _buildPayload(IncludeCharacterName && HasCharacterName);
        return _renderer.RenderCardAsync(payload, _getGourmandLevel());
    }

    private Task<BitmapSource> BuildFullGridAsync()
    {
        var payload = _buildPayload(IncludeCharacterName && HasCharacterName);
        return _renderer.RenderFullGridAsync(payload, _getGourmandLevel());
    }

    /// <summary>Render off the UI thread, surface progress via <see cref="IsRendering"/>,
    /// then dispatch the bitmap on the UI thread (clipboard / save dialog calls require it).</summary>
    private async Task RenderThen(Func<Task<BitmapSource>> build, Action<BitmapSource> dispatchOnUi)
    {
        if (IsRendering) return;
        IsRendering = true;
        StatusMessage = "Rendering image…";
        try
        {
            var image = await build().ConfigureAwait(true);
            dispatchOnUi(image);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Render failed: {ex.Message}";
        }
        finally
        {
            IsRendering = false;
        }
    }

    private void CopyText(string text, string okMessage)
    {
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
            StatusMessage = okMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not copy to clipboard: {ex.Message}";
        }
    }

    private void CopyImage(BitmapSource image, string okMessage)
    {
        try
        {
            // copy: true persists the bitmap on the clipboard past Mithril's process
            // lifetime — the user can copy → close Mithril → paste in Discord.
            Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, image), copy: true);
            StatusMessage = okMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not copy image: {ex.Message}";
        }
    }

    private void SaveImage(BitmapSource image, string variant)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Pippin Progress Image",
            Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
            DefaultExt = "png",
            FileName = $"pippin-progress-{variant}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.png",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var fs = File.Create(dialog.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(fs);
            StatusMessage = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }
}
