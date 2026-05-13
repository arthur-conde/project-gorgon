namespace Silmarillion.ViewModels;

/// <summary>
/// Marshals a callback to the WPF UI thread when one is available, or invokes it
/// inline when no <see cref="System.Windows.Application"/> is hosted (tests). Used
/// by the tab view-models to handle <see cref="Mithril.Shared.Reference.IReferenceDataService.FileUpdated"/>
/// events, which fire from the background refresh task and must apply collection
/// mutations on the UI thread.
/// </summary>
internal static class UiThread
{
    public static void Run(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.BeginInvoke(action);
    }
}
