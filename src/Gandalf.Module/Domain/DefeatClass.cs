namespace Gandalf.Domain;

/// <summary>
/// Distinguishes how the game enforces a boss's reward cooldown — captured in
/// <see cref="DefeatCatalogEntry.Class"/> so <see cref="Services.LootSource"/>
/// can route observations through the right parser path.
/// </summary>
public enum DefeatClass
{
    /// <summary>
    /// Game refuses re-kills outright while the cooldown is active — no kill
    /// credit, no loot bracket, no <c>[60, ]</c> post-kill effect. The positive
    /// signal is the corpse-search line + loot bracket; the negative signal is
    /// the <c>"You have already killed &lt;Name&gt; too recently."</c> screen text.
    /// Megaspider is the prototype.
    /// </summary>
    DefeatCooldown = 0,

    /// <summary>
    /// Game delivers full kill credit + loot on every kill regardless of
    /// cooldown. Cooldown gating is local-only (in-flight suppression) so a
    /// within-window re-kill doesn't reset the clock. Olugax The Ever-Pudding
    /// is the prototype.
    /// </summary>
    ScriptedEvent = 1,
}
