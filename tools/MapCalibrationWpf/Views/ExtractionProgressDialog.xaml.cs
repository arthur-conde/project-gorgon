namespace Mithril.Tools.MapCalibrationWpf.Views;

using System.Windows;

/// <summary>
/// Modal progress indicator for the in-process asset extractors. The owning
/// VM forwards <see cref="IProgress{T}"/> ticks to <see cref="UpdateStatus"/>.
/// <see cref="CompleteAndClose"/> dismisses the dialog on success; on failure
/// the caller surfaces the error separately and calls <see cref="Close"/>.
/// </summary>
public partial class ExtractionProgressDialog : Window
{
    public ExtractionProgressDialog(string initialStatus)
    {
        InitializeComponent();
        StatusText.Text = initialStatus;
    }

    public void UpdateStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => StatusText.Text = status);
            return;
        }
        StatusText.Text = status;
    }

    public void CompleteAndClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(CompleteAndClose);
            return;
        }
        DialogResult = true;
        Close();
    }
}
