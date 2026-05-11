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
    /// <summary>
    /// Navigate to the detail view for <paramref name="reference"/>.
    /// Returns <see langword="void"/> by design — this is a fire-and-forget navigation
    /// command, not a request for an embedded view.
    /// </summary>
    void Open(EntityRef reference);
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
}
