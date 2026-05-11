using Microsoft.Extensions.Logging;

namespace Mithril.Shared.Reference;

/// <summary>
/// Cross-module navigation contract. Call <see cref="Open"/> to direct the application
/// to the detail view for an entity. The active implementation decides how (new panel,
/// tab switch, overlay, etc.); callers are deliberately decoupled from that choice.
/// </summary>
/// <remarks>
/// The shell registers <see cref="NoOpReferenceNavigator"/> as the default singleton.
/// The DB module (#207) replaces this with a real implementation via its own
/// <c>Register(IServiceCollection)</c>; the later <c>AddSingleton</c> call wins for
/// non-keyed resolution.
/// </remarks>
public interface IReferenceNavigator
{
    // ---- Navigation commands ---------------------------------------------------

    /// <summary>
    /// Navigate to the detail view for <paramref name="reference"/>.
    /// Pushes <paramref name="reference"/> onto the back stack, clears the forward stack,
    /// sets <see cref="Current"/> to <paramref name="reference"/>, and fires
    /// <see cref="Navigated"/> with <see cref="NavigationKind.Open"/>.
    /// Returns <see langword="void"/> by design — this is a fire-and-forget navigation
    /// command, not a request for an embedded view.
    /// </summary>
    void Open(EntityRef reference);

    /// <summary>
    /// Navigate backward in history.
    /// Pops the back stack onto the forward stack, updates <see cref="Current"/>, and fires
    /// <see cref="Navigated"/> with <see cref="NavigationKind.Back"/>.
    /// No-op if <see cref="CanGoBack"/> is <see langword="false"/>.
    /// </summary>
    void Back();

    /// <summary>
    /// Navigate forward in history.
    /// Pops the forward stack onto the back stack, updates <see cref="Current"/>, and fires
    /// <see cref="Navigated"/> with <see cref="NavigationKind.Forward"/>.
    /// No-op if <see cref="CanGoForward"/> is <see langword="false"/>.
    /// </summary>
    void Forward();

    // ---- State properties ------------------------------------------------------

    /// <summary>
    /// The entity currently displayed, or <see langword="null"/> if no navigation has occurred.
    /// </summary>
    EntityRef? Current { get; }

    /// <summary>
    /// <see langword="true"/> when the back stack is non-empty and <see cref="Back"/> would do work.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// <see langword="true"/> when the forward stack is non-empty and <see cref="Forward"/> would do work.
    /// </summary>
    bool CanGoForward { get; }

    /// <summary>
    /// Returns <see langword="true"/> if this implementation can display the detail view for
    /// <paramref name="reference"/>. Drives cross-link click-vs-text rendering: when
    /// <see langword="false"/>, callers should render the reference as plain text rather
    /// than a clickable hyperlink.
    /// </summary>
    bool CanOpen(EntityRef reference);

    // ---- Events ----------------------------------------------------------------

    /// <summary>
    /// Raised after every navigation that changes <see cref="Current"/>.
    /// View subscribers re-evaluate command states and refresh the master-detail display.
    /// Never raised by <see cref="NoOpReferenceNavigator"/>.
    /// </summary>
    event EventHandler<NavigatedEventArgs>? Navigated;
}

/// <summary>
/// No-op implementation registered by the shell until a real navigator module is present.
/// Logs at <c>Information</c> level so missing-module cases are diagnosable without being noisy.
/// </summary>
public sealed class NoOpReferenceNavigator : IReferenceNavigator
{
    private readonly ILogger<NoOpReferenceNavigator> _logger;

    public NoOpReferenceNavigator(ILogger<NoOpReferenceNavigator> logger) => _logger = logger;

    /// <inheritdoc />
    public void Open(EntityRef reference) =>
        _logger.LogInformation(
            "IReferenceNavigator.Open({Kind}, {InternalName}) — no module registered",
            reference.Kind, reference.InternalName);

    /// <inheritdoc />
    public void Back() =>
        _logger.LogInformation("IReferenceNavigator.Back() — no module registered");

    /// <inheritdoc />
    public void Forward() =>
        _logger.LogInformation("IReferenceNavigator.Forward() — no module registered");

    /// <inheritdoc />
    public EntityRef? Current => null;

    /// <inheritdoc />
    public bool CanGoBack => false;

    /// <inheritdoc />
    public bool CanGoForward => false;

    /// <inheritdoc />
    public bool CanOpen(EntityRef reference) => false;

    /// <inheritdoc />
    /// <remarks>Never fired by <see cref="NoOpReferenceNavigator"/>.</remarks>
    public event EventHandler<NavigatedEventArgs>? Navigated
    {
        add { /* no-op: no module registered */ }
        remove { /* no-op: no module registered */ }
    }
}
