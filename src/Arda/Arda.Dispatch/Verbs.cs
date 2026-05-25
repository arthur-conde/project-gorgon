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

    // Standard LocalPlayer: Process* verbs (literal text from log)
    public const string ProcessAddItem = "ProcessAddItem";
    public const string ProcessDeleteItem = "ProcessDeleteItem";
    public const string ProcessUpdateItemCode = "ProcessUpdateItemCode";
    public const string ProcessLoadSkills = "ProcessLoadSkills";
    public const string ProcessUpdateSkill = "ProcessUpdateSkill";
    public const string ProcessLoadRecipes = "ProcessLoadRecipes";
    public const string ProcessUpdateRecipe = "ProcessUpdateRecipe";
    public const string ProcessStartInteraction = "ProcessStartInteraction";

    // Chat log synthetic verbs
    /// <summary>Synthetic verb for <c>**** Logged In As &lt;char&gt;. Server &lt;server&gt;. ...</c></summary>
    public const string ChatLoginBanner = "CHAT_LOGIN_BANNER";

    /// <summary>Synthetic verb for <c>[Status] X [xN] added to inventory.</c></summary>
    public const string StatusInventory = "STATUS_INVENTORY";

    /// <summary>Synthetic verb for <c>[Channel] Speaker: text</c> player chat lines.</summary>
    public const string ChatPlayerLine = "CHAT_PLAYER_LINE";
}
