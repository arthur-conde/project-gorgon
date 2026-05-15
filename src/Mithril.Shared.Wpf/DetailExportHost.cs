using System.Windows;
using System.Windows.Controls;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Wraps a Silmarillion entity detail card and overlays a "Copy as image" camera
/// button (top-right). Clicking it snapshots <em>only</em> the wrapped content:
/// the button lives in the control template as a sibling of the content presenter,
/// so it is never part of the captured visual — no hide/restore dance needed.
/// <para>
/// Because every detail card (Ability, Quest, …, Item) wraps its body in this host,
/// the affordance appears wherever the view is shown — the inline master-detail
/// pane <em>and</em> the standalone <c>*DetailWindow</c> popups — from one place.
/// Default style ships in <c>Mithril.Shared.Wpf/Resources.xaml</c> (mirrors the
/// <see cref="ViewModeToggle"/> templated-control pattern).
/// </para>
/// </summary>
public sealed class DetailExportHost : ContentControl
{
    static DetailExportHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DetailExportHost), new FrameworkPropertyMetadata(typeof(DetailExportHost)));
    }

    private Button? _exportButton;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_exportButton is not null)
            _exportButton.Click -= OnExportClick;

        _exportButton = GetTemplateChild("PART_ExportButton") as Button;

        if (_exportButton is not null)
            _exportButton.Click += OnExportClick;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        // Snapshot the content presenter only. The button is its sibling in the
        // template Grid, so it is excluded from the captured visual implicitly.
        if (GetTemplateChild("PART_ExportContent") is not FrameworkElement content
            || _exportButton is null)
            return;

        var ok = VisualImageExporter.CopyToClipboard(content);
        DetailExportFeedback.Run(ok, _exportButton);
    }
}
