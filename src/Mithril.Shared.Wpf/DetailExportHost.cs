using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Wraps a Silmarillion entity detail card. Two responsibilities, both kept off the
/// individual views so they stay consistent in one place:
/// <list type="bullet">
///   <item>
///     A "Copy as image" camera button (top-right overlay). Snapshots <em>only</em>
///     the wrapped content + footer — the camera button is a sibling of
///     <c>PART_ExportContent</c> in the template, so it is excluded from the capture
///     with no hide/restore dance.
///   </item>
///   <item>
///     The mono bottom-right internal-name footer (<see cref="FooterText"/>), shown
///     only when set (Effect/StorageVault pass nothing → no footer). Click-to-copy
///     with a transient "copied" ack. Folding it here is also what makes the export
///     <em>tightly sized</em>: the host stacks [content desired-height] + [footer]
///     with no pane filler, replacing the old per-view footer + viewport-pin trick.
///   </item>
/// </list>
/// Appears wherever the view is hosted — the inline master-detail pane and the
/// standalone <c>*DetailWindow</c> popups. Default style ships in
/// <c>Mithril.Shared.Wpf/Resources.xaml</c> (mirrors the <see cref="ViewModeToggle"/>
/// templated-control pattern).
/// </summary>
public sealed class DetailExportHost : ContentControl
{
    public static readonly DependencyProperty FooterTextProperty = DependencyProperty.Register(
        nameof(FooterText), typeof(string), typeof(DetailExportHost),
        new PropertyMetadata(null));

    /// <summary>Internal-name (or envelope-key) footer. Null/empty hides the footer.</summary>
    public string? FooterText
    {
        get => (string?)GetValue(FooterTextProperty);
        set => SetValue(FooterTextProperty, value);
    }

    private static readonly DependencyPropertyKey FooterCopiedKey =
        DependencyProperty.RegisterReadOnly(
            nameof(FooterCopied), typeof(bool), typeof(DetailExportHost),
            new PropertyMetadata(false));

    /// <summary>True for ~1.2 s after the footer is clicked; the template swaps the
    /// footer label to a "copied" acknowledgement while set.</summary>
    public static readonly DependencyProperty FooterCopiedProperty = FooterCopiedKey.DependencyProperty;

    public bool FooterCopied => (bool)GetValue(FooterCopiedProperty);

    private static readonly TimeSpan AckHold = TimeSpan.FromMilliseconds(1200);

    static DetailExportHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DetailExportHost), new FrameworkPropertyMetadata(typeof(DetailExportHost)));
    }

    private Button? _exportButton;
    private Button? _footerButton;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_exportButton is not null)
            _exportButton.Click -= OnExportClick;
        if (_footerButton is not null)
            _footerButton.Click -= OnFooterClick;

        _exportButton = GetTemplateChild("PART_ExportButton") as Button;
        _footerButton = GetTemplateChild("PART_FooterButton") as Button;

        if (_exportButton is not null)
            _exportButton.Click += OnExportClick;
        if (_footerButton is not null)
            _footerButton.Click += OnFooterClick;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        // Snapshot PART_ExportContent (footer + body). The camera button is its
        // sibling in the template Grid, so it is excluded from the capture implicitly.
        if (GetTemplateChild("PART_ExportContent") is not FrameworkElement content
            || _exportButton is null)
            return;

        var ok = VisualImageExporter.CopyToClipboard(content);
        DetailExportFeedback.Run(ok, _exportButton);
    }

    private void OnFooterClick(object sender, RoutedEventArgs e)
    {
        var text = FooterText;
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
        }
        catch
        {
            return; // clipboard can transiently fail; no ack, user can retry
        }

        SetValue(FooterCopiedKey, true);
        var timer = new DispatcherTimer { Interval = AckHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SetValue(FooterCopiedKey, false);
        };
        timer.Start();
    }
}
