using System.Windows.Controls;

namespace Legolas.Views;

public partial class WizardView : UserControl
{
    public WizardView() => InitializeComponent();

    /// <summary>
    /// Auto-scroll the survey ListBox so the freshly-selected pin is visible.
    /// Selection changes on three paths: log-ingestion auto-selects the latest
    /// arriving survey, the user clicks a row directly (already in view), and
    /// the nudge hotkeys / pad change pin focus. The first and third can leave
    /// the selected pin off-screen for long survey runs (100+ pins), so a
    /// simple ScrollIntoView is the QoL win.
    /// </summary>
    private void SurveyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is not null)
        {
            listBox.ScrollIntoView(listBox.SelectedItem);
        }
    }
}
