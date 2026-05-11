namespace Mithril.Shared.Logging;

/// <summary>
/// Concrete leaf implementation of <see cref="ISessionAnchor"/>. Holds the
/// most recent <c>LoggedInUtc</c> instant and raises <see cref="AnchorChanged"/>
/// on every transition.
///
/// <para><b>Why a separate class instead of having <c>GameSessionService</c>
/// implement <see cref="ISessionAnchor"/> directly</b>: the streams that
/// consume the anchor (<c>PlayerLogStream</c>, <c>ChatLogStream</c>) live in
/// <c>Mithril.Shared</c>, and <c>GameSessionService</c> in turn consumes
/// <c>IPlayerLogStream</c>. Wiring the anchor through the service forms a DI
/// cycle (stream → anchor → service → stream). Splitting the anchor out as a
/// leaf class breaks the cycle: both <c>PlayerLogStream</c> and
/// <c>GameSessionService</c> consume the leaf; only the service writes to it.</para>
/// </summary>
public sealed class SessionAnchor : ISessionAnchor
{
    private readonly object _lock = new();
    private DateTime? _loggedInUtc;

    public DateTime? LoggedInUtc
    {
        get { lock (_lock) return _loggedInUtc; }
    }

    public event EventHandler? AnchorChanged;

    /// <summary>
    /// Set the current session anchor. No-op (no <see cref="AnchorChanged"/>
    /// fire) when the value is unchanged — keeps replay-on-relaunch quiet at
    /// the anchor layer the same way <c>GameSessionService</c> stays quiet at
    /// the session layer.
    /// </summary>
    public void SetLoggedInUtc(DateTime loggedInUtc)
    {
        bool changed;
        lock (_lock)
        {
            changed = _loggedInUtc != loggedInUtc;
            _loggedInUtc = loggedInUtc;
        }
        if (changed) AnchorChanged?.Invoke(this, EventArgs.Empty);
    }
}
