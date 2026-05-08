using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Microsoft.Win32;
using Mithril.Shared.Reference;
using Mithril.Shared.Sharing;
using Mithril.Shared.Wpf.Dialogs;

namespace Legolas.Sharing;

/// <summary>
/// Dialog for the end-of-run survey report. Image-as-hero layout: the rendered
/// 1000×400 card is the headline; copy/save buttons act on the image, the text
/// summary, the JSON payload, or the <c>mithril://legolas/&lt;base64url&gt;</c>
/// share link. The same dialog serves both ends of the share flow — the wizard
/// uses it after Done, and <see cref="LegolasShareImportTarget"/> uses it when a
/// deep link is opened, so receiver and sender see identical artifacts.
///
/// The card preview re-renders whenever the payload changes (i.e. when the
/// "Include character name" toggle flips). Render runs on a background STA worker
/// thread; the bitmap is frozen before it returns, so the UI thread can swap it
/// in safely.
/// </summary>
public sealed partial class LegolasShareDialogViewModel : DialogViewModelBase
{
    private readonly LegolasShareCardRenderer? _renderer;
    private readonly LegolasSettings _settings;
    private readonly Func<bool, LegolasSharePayload> _buildPayload;
    private readonly IReferenceDataService? _refData;

    public LegolasShareDialogViewModel(
        Func<bool, LegolasSharePayload> buildPayload,
        LegolasShareCardRenderer? renderer,
        LegolasSettings settings,
        bool hasCharacterName,
        bool showCharacterNameToggle = true,
        IReferenceDataService? refData = null)
    {
        _buildPayload = buildPayload;
        _renderer = renderer;
        _settings = settings;
        _refData = refData;
        HasCharacterName = hasCharacterName;
        // Anonymizing someone else's identity isn't a meaningful affordance for
        // a receiver — they didn't pick the name. The import target passes
        // showCharacterNameToggle=false so the checkbox stays hidden.
        ShowCharacterNameToggle = showCharacterNameToggle && hasCharacterName;
        _includeCharacterName = hasCharacterName;
        Refresh();
    }

    public override string Title => "Survey complete";
    public override string PrimaryButtonText => "Close";
    public override string? SecondaryButtonText => null;

    // Card preview is 1000×400; outer host needs room for the image plus dialog
    // chrome (title bar, button row, content padding). 1100 leaves the card at
    // near-1:1 with breathing room on either side.
    public override double MaxContentWidth => 1100;

    public bool HasCharacterName { get; }
    public bool ShowCharacterNameToggle { get; }
    public bool HasRenderer => _renderer is not null;

    [ObservableProperty] private bool _includeCharacterName;
    [ObservableProperty] private string _shareLink = "";
    [ObservableProperty] private string _jsonPayload = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private BitmapSource? _cardPreview;
    [ObservableProperty] private bool _isRenderingPreview;

    /// <summary>
    /// Cached payload for the current toggle state. Populated by <see cref="Refresh"/>
    /// so copy/save commands operate on a stable snapshot rather than re-invoking
    /// <see cref="_buildPayload"/> mid-action (which would be racy with a fresh
    /// FSM state on the sender side).
    /// </summary>
    private LegolasSharePayload _currentPayload = new();

    partial void OnIncludeCharacterNameChanged(bool value) => Refresh();

    private void Refresh()
    {
        _currentPayload = _buildPayload(IncludeCharacterName && HasCharacterName);
        JsonPayload = JsonSerializer.Serialize(_currentPayload, LegolasShareJsonContext.Default.LegolasSharePayload);
        ShareLink = "mithril://legolas/" + ShareCodec.EncodePayload(JsonPayload);
        Summary = LegolasReportService.BuildSummary(_currentPayload, _refData);
        // Kick off a fresh card render. Don't await — the render is fire-and-forget;
        // the UI binds to CardPreview which is updated on completion.
        _ = RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        if (_renderer is null) return;
        IsRenderingPreview = true;
        try
        {
            var image = await _renderer.RenderCardAsync(_currentPayload).ConfigureAwait(true);
            CardPreview = image;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview render failed: {ex.Message}";
        }
        finally
        {
            IsRenderingPreview = false;
        }
    }

    [RelayCommand] private void CopyLink() => CopyText(ShareLink, "Share link copied to clipboard.");
    [RelayCommand] private void CopyJson() => CopyText(JsonPayload, "JSON copied to clipboard.");
    [RelayCommand] private void CopySummary() => CopyText(Summary, "Summary copied to clipboard.");

    [RelayCommand]
    private void CopyImage()
    {
        if (CardPreview is null)
        {
            StatusMessage = "Preview not ready yet.";
            return;
        }
        try
        {
            // copy: true persists the bitmap on the clipboard past Mithril's
            // lifetime — paste in Discord still works after closing.
            Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, CardPreview), copy: true);
            StatusMessage = "Card image copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not copy image: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveImage()
    {
        if (CardPreview is null)
        {
            StatusMessage = "Preview not ready yet.";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Save Legolas Report Image",
            Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
            DefaultExt = "png",
            FileName = $"legolas-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.png",
            InitialDirectory = _settings.ReportSaveDirectory ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var fs = File.Create(dialog.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CardPreview));
            encoder.Save(fs);
            _settings.ReportSaveDirectory = Path.GetDirectoryName(dialog.FileName);
            StatusMessage = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveJson()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Legolas Report",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = $"legolas-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.json",
            InitialDirectory = _settings.ReportSaveDirectory ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonPayload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _settings.ReportSaveDirectory = Path.GetDirectoryName(dialog.FileName);
            StatusMessage = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
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
}
