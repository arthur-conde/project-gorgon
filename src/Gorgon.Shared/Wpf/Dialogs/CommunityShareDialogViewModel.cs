using System.Diagnostics;
using System.IO;
using System.Web;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Gorgon.Shared.Wpf.Dialogs;

/// <summary>
/// Reusable "Share calibration with community" dialog used by Samwise and Arwen.
/// Handles the contributor-note textbox, JSON preview, and the two submission flows
/// (Copy + Open Issue, Save to File). No module-specific code lives here — callers
/// supply the module's export function and issue-template name.
/// </summary>
public sealed partial class CommunityShareDialogViewModel : DialogViewModelBase
{
    private const string IssueNewUrlTemplate =
        "https://github.com/arthur-conde/gorgon-calibration/issues/new?template={0}&body={1}";

    // Leave ~6 KB safety room under the typical browser URL cap (~8 KB). If the encoded
    // payload is larger, open the issue with a minimal body and rely on clipboard paste.
    private const int MaxPreFilledBodyBytes = 6_000;

    private readonly string _moduleDisplayName;
    private readonly string _issueTemplateFile;
    private readonly Func<string?, string> _exportJson;

    public CommunityShareDialogViewModel(
        string moduleDisplayName,
        string issueTemplateFile,
        Func<string?, string> exportJson)
    {
        _moduleDisplayName = moduleDisplayName;
        _issueTemplateFile = issueTemplateFile;
        _exportJson = exportJson;
        RefreshPreview();
    }

    public override string Title => $"Share {_moduleDisplayName} Calibration";
    public override string PrimaryButtonText => "Close";
    public override string? SecondaryButtonText => null;

    [ObservableProperty]
    private string _contributorNote = "";

    [ObservableProperty]
    private string _jsonPreview = "";

    [ObservableProperty]
    private string _statusMessage = "";

    partial void OnContributorNoteChanged(string value) => RefreshPreview();

    private void RefreshPreview()
    {
        JsonPreview = _exportJson(string.IsNullOrWhiteSpace(ContributorNote) ? null : ContributorNote);
    }

    [RelayCommand]
    private void CopyAndOpenIssue()
    {
        try
        {
            Clipboard.SetText(JsonPreview);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not copy to clipboard: {ex.Message}";
            return;
        }

        // Only pre-fill the issue body if it's under the URL-length safety margin. Otherwise
        // open with a minimal prompt asking the user to paste from the clipboard.
        var encoded = HttpUtility.UrlEncode(JsonPreview);
        string body;
        string tail;
        if (encoded.Length <= MaxPreFilledBodyBytes)
        {
            body = encoded;
            tail = "JSON copied to clipboard and pre-filled in the issue body.";
        }
        else
        {
            body = HttpUtility.UrlEncode(
                "(Payload too large to pre-fill.) Paste the JSON from your clipboard below this line.");
            tail = "JSON copied to clipboard — paste it into the issue body in your browser.";
        }

        var url = string.Format(IssueNewUrlTemplate, _issueTemplateFile, body);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusMessage = tail;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copied to clipboard, but could not open browser: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveToFile()
    {
        var dialog = new SaveFileDialog
        {
            Title = $"Save {_moduleDisplayName} Calibration",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = $"{_moduleDisplayName.ToLowerInvariant()}-calibration-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonPreview);
            StatusMessage = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }
}
