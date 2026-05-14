namespace Silmarillion.ViewModels;

/// <summary>
/// Tab-VM contract for the Silmarillion top-level <see cref="SilmarillionViewModel"/>.
/// Each tab VM implements this so the shell view-model can compose its <c>Tabs</c>
/// collection from <c>IEnumerable&lt;ITabViewModel&gt;</c> rather than positional
/// per-tab constructor parameters. Adding a new tab is then a DI registration only;
/// no <see cref="SilmarillionViewModel"/> ctor change and no rippling <c>null!</c>
/// edits in tests.
/// </summary>
public interface ITabViewModel
{
    /// <summary>Header text rendered in the <c>TabControl</c> chrome.</summary>
    string TabHeader { get; }

    /// <summary>
    /// Sort key that determines tab order. Must match
    /// <see cref="Mithril.Shared.Reference.IReferenceKindTarget.TabIndex"/> for any
    /// entity kinds dispatched to this tab — see <c>SilmarillionViewModel.OnNavigated</c>.
    /// </summary>
    int TabOrder { get; }
}
