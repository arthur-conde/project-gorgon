using Mithril.Reference.Models.Npcs;

namespace Mithril.Shared.Reference;

/// <summary>
/// Cross-module lookup for the active character's favor state with a given NPC.
/// Arwen's <c>FavorStateService</c> is the canonical producer; other modules
/// (Smaug, etc.) consume this optional dependency so they can gate UI on
/// the player's current favor without a direct reference to Arwen.
/// </summary>
public interface IFavorLookupService
{
    /// <summary>
    /// The known favor tier for the active character with this NPC, as the
    /// canonical <see cref="FavorTier"/>. Returns null when the player has not
    /// interacted with the NPC or no favor data is available (distinct from
    /// <see cref="FavorTier.Unknown"/>, which means an unparseable token).
    /// </summary>
    FavorTier? GetFavorTier(string npcKey);

    /// <summary>Fires when any NPC's favor tier may have changed.</summary>
    event EventHandler? FavorChanged;
}
