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
/// <remarks>
/// Implementers SHOULD also implement non-generic
/// <see cref="System.Collections.IList"/> (with mutators throwing
/// <see cref="NotSupportedException"/> via explicit-interface
/// implementations) if they expect downstream consumers to wrap the surface
/// in WPF's <c>CollectionViewSource</c> for sort, filter, or group support.
/// WPF reflects on the runtime type when resolving the default view; without
/// <c>IList</c>, <c>CollectionViewSource.GetDefaultView</c> falls back to
/// <c>EnumerableCollectionView</c>, which does not support sort, filter, or
/// group. See <c>System.Collections.ObjectModel.ReadOnlyObservableCollection&lt;T&gt;</c>
/// for the canonical BCL pattern (read-only <see cref="System.Collections.IList"/>
/// over an <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>).
/// </remarks>
public interface IReadOnlyObservableCollection<out T>
    : IReadOnlyList<T>, INotifyCollectionChanged, INotifyPropertyChanged
{
}
