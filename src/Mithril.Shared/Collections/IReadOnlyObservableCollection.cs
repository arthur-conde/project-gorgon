using System.Collections.Specialized;
using System.ComponentModel;

namespace Mithril.Shared.Collections;

/// <summary>
/// Read-only collection surface for WPF data-binding: combines indexed access
/// (<see cref="IReadOnlyList{T}"/>) with the two notification interfaces a XAML
/// <c>ItemsControl</c> consumes — <see cref="INotifyCollectionChanged"/> for
/// add/remove/reset, <see cref="INotifyPropertyChanged"/> for the standard
/// <c>Count</c> / <c>Item[]</c> notifications that
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> emits.
///
/// <para>This is the "Bind" channel of the three-channel state-service
/// contract described in <c>docs/module-charters.md</c>: collection-shaped
/// services expose <c>IReadOnlyObservableCollection&lt;T&gt;</c> so UI code
/// binds to canonical state instead of re-mirroring an event stream into a
/// local <c>ObservableCollection</c>.</para>
///
/// <para>The intentional alias of the BCL-shaped contract — there is no
/// <c>IReadOnlyObservableCollection&lt;T&gt;</c> in the framework, but the
/// observable / read-only / list axes compose naturally for any read-only
/// view over an <c>ObservableCollection</c>. Implementers are free to back
/// the surface with a wrapped <c>ObservableCollection&lt;T&gt;</c>, a custom
/// keyed observable collection, or any other source that fires the two
/// notification interfaces consistently.</para>
///
/// <para><b>Threading.</b> Implementers document where mutations are raised
/// (background ingestion thread vs marshalled to the UI dispatcher). WPF
/// consumers binding from a non-dispatcher thread must call
/// <c>BindingOperations.EnableCollectionSynchronization</c> with the same
/// lock the implementer mutates under.</para>
/// </summary>
public interface IReadOnlyObservableCollection<out T>
    : IReadOnlyList<T>, INotifyCollectionChanged, INotifyPropertyChanged
{
}
