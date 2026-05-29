namespace Mithril.Tools.MapCalibrationWpf.Views;

using System.IO;
using System.Windows;
using Microsoft.Win32;

/// <summary>
/// Modal that prompts the user to point at the Project Gorgon install folder
/// when <see cref="Services.PgInstallResolver"/> can't auto-detect it.
/// Validates that the chosen folder contains <c>WindowsPlayer_Data</c> before
/// accepting — the asset extractors will fail later with a less-helpful message
/// if we let through a wrong folder here.
/// </summary>
public partial class PgInstallPickerDialog : Window
{
    public string? SelectedPath { get; private set; }

    public PgInstallPickerDialog(string? initialPath = null)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(initialPath))
        {
            PathTextBox.Text = initialPath;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        // .NET 8+ ships OpenFolderDialog in System.Windows; no need for the
        // legacy WinForms FolderBrowserDialog interop.
        var dlg = new OpenFolderDialog
        {
            Title = "Select the Project Gorgon install folder",
            InitialDirectory = PathTextBox.Text,
        };
        if (dlg.ShowDialog(this) == true)
        {
            PathTextBox.Text = dlg.FolderName;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var path = PathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            ErrorText.Text = "Path does not exist.";
            return;
        }
        // Light sanity check — the canonical PG layout has WindowsPlayer_Data
        // immediately under the install root. If that's missing the downstream
        // asset extractors will fail with a less informative message.
        if (!Directory.Exists(Path.Combine(path, "WindowsPlayer_Data")))
        {
            ErrorText.Text = "That folder doesn't look like a PG install (no WindowsPlayer_Data subfolder).";
            return;
        }
        SelectedPath = path;
        DialogResult = true;
        Close();
    }
}
