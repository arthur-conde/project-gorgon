namespace Mithril.Shell.Views;

/// <summary>
/// The Diagnostics → Telemetry export sub-section. Renders
/// <see cref="ViewModels.TelemetrySettingsViewModel"/> — enable toggle,
/// endpoint/protocol/service-name fields, headers DataGrid, exported-tags
/// chip cloud, newly-seen panel, and last-export status. Hosted by
/// <see cref="DiagnosticsSettingsView"/> via DataContext binding.
/// </summary>
public partial class TelemetrySettingsControl : System.Windows.Controls.UserControl
{
    public TelemetrySettingsControl()
    {
        InitializeComponent();
    }
}
