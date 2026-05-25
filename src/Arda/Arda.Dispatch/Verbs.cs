namespace Arda.Dispatch;

/// <summary>
/// Canonical verb keys used by <see cref="VerbExtractor"/> and handler registrations.
/// Synthetic keys (for non-Process* system lines) are prefixed with their structural origin.
/// </summary>
public static class Verbs
{
    /// <summary>Synthetic verb for "LOADING LEVEL AreaKey" system lines.</summary>
    public const string LoadingLevel = "LOADING_LEVEL";

    /// <summary>Synthetic verb for "!!! Initializing area! (id): AreaKey" system lines.</summary>
    public const string InitializingArea = "InitializingArea";
}
