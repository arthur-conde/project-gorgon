namespace Mithril.Shared.Modules;

/// <summary>
/// Optional target that handles <c>mithril://elrond/&lt;skillKey&gt;</c> deep links.
/// Implemented by Elrond to bring its tab to the foreground and pre-select the
/// named skill in the advisor. The router treats it as optional — if no module
/// has registered a handler, elrond links are logged and dropped.
/// </summary>
public interface IElrondSkillImportTarget
{
    /// <summary>
    /// Activates Elrond and selects the named skill. <paramref name="skillKey"/>
    /// is the skill internal id (e.g. <c>"Cheesemaking"</c>, <c>"ArmorAugmentBrewing"</c>),
    /// matching <c>SkillEntry.Key</c> and recipes' <c>RewardSkill</c> field. All
    /// errors are logged and swallowed — callers are OS activation handlers that
    /// mustn't throw.
    /// </summary>
    void ImportFromLinkPayload(string skillKey);
}
