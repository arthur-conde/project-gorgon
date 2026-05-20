using System.Windows.Threading;

namespace Mithril.Shared.Logging;

/// <summary>
/// L1 per-subscription delivery context (#511 deliverable 3 / #550 capability E).
/// Determines on which thread the consumer's handler runs. A composition
/// parameter on the subscription, not a layer.
///
/// <para><see cref="Marshaled"/> structurally kills the cross-thread
/// <c>ObservableCollection</c> mutation bug class: the driver routes each
/// envelope through the supplied <see cref="Dispatcher"/> before invoking
/// the handler, so a consumer that mutates bound collections inside its
/// handler is by construction on the UI thread. The migration PR to
/// follow replaces the five hand-rolled <c>Application.Current?.Dispatcher;
/// CheckAccess; InvokeAsync</c> helpers across module ingestion services
/// with a single <see cref="Marshaled"/> option on the subscription.</para>
///
/// <para><b>Honest boundary:</b> this covers <em>stream → consumer</em>
/// delivery only. It does NOT subsume the GameState producers' internal
/// snapshot-under-lock <c>Subscribe</c> fan-out (<c>PlayerSkillStateService</c>,
/// <c>PlayerRecipeStateService</c>, etc.) — that is a different mechanism
/// (state snapshot delivery, not log-line delivery) and keeps its own
/// marshalling.</para>
/// </summary>
public abstract record DeliveryContext
{
    private DeliveryContext() { }

    /// <summary>
    /// Invoke the handler synchronously on whichever thread the driver
    /// happens to be pumping on (the channel-reader thread, today). The
    /// caller is responsible for any thread-affinity requirements — typical
    /// for handlers that mutate only thread-safe state or self-dispatch
    /// downstream (Gandalf, Saruman). Default.
    /// </summary>
    public sealed record InlineContext : DeliveryContext;

    /// <summary>
    /// Marshal each envelope through <paramref name="Dispatcher"/> before
    /// invoking the handler. The handler runs on the dispatcher's thread —
    /// for the typical WPF case <c>Application.Current.Dispatcher</c>, the
    /// UI thread — so a handler that mutates bound <c>ObservableCollection</c>s
    /// is on the right thread by construction.
    /// </summary>
    public sealed record MarshaledContext(Dispatcher Dispatcher) : DeliveryContext;

    /// <summary>Inline (default) singleton.</summary>
    public static readonly DeliveryContext Inline = new InlineContext();

    /// <summary>Marshal each envelope through the supplied dispatcher.</summary>
    public static DeliveryContext Marshaled(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return new MarshaledContext(dispatcher);
    }
}
