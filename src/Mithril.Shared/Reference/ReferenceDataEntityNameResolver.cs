namespace Mithril.Shared.Reference;

/// <summary>
/// Default <see cref="IEntityNameResolver"/> — resolves names via the live
/// <see cref="IReferenceDataService"/> indices. NPC envelope keys (e.g. <c>"NPC_Joeh"</c>,
/// <c>"Altar_Druid"</c>) fall back to a leading <c>"NPC_"</c> strip so unnamed altars /
/// placeholders read as <c>"SpiderPlaceholder"</c> rather than <c>"NPC_SpiderPlaceholder"</c>.
/// Items and Recipes fall through to the raw internal name when no POCO is registered or
/// its <c>Name</c> field is empty. Unrecognised kinds (e.g. a freshly-added
/// <see cref="EntityKind"/> whose case isn't wired in yet) return the raw internal name.
/// </summary>
public sealed class ReferenceDataEntityNameResolver : IEntityNameResolver
{
    private readonly IReferenceDataService _refData;

    public ReferenceDataEntityNameResolver(IReferenceDataService refData) => _refData = refData;

    public string Resolve(EntityRef reference) => reference.Kind switch
    {
        EntityKind.Item => ResolveItem(reference.InternalName),
        EntityKind.Recipe => ResolveRecipe(reference.InternalName),
        EntityKind.Npc => ResolveNpc(reference.InternalName),
        EntityKind.Quest => ResolveQuest(reference.InternalName),
        EntityKind.Ability => ResolveAbility(reference.InternalName),
        EntityKind.Effect => ResolveEffect(reference.InternalName),
        EntityKind.Skill => ResolveSkill(reference.InternalName),
        _ => reference.InternalName,
    };

    private string ResolveItem(string internalName) =>
        _refData.ItemsByInternalName.TryGetValue(internalName, out var item) && !string.IsNullOrEmpty(item.Name)
            ? item.Name!
            : internalName;

    private string ResolveRecipe(string internalName) =>
        _refData.RecipesByInternalName.TryGetValue(internalName, out var recipe) && !string.IsNullOrEmpty(recipe.Name)
            ? recipe.Name!
            : internalName;

    private string ResolveNpc(string internalName) =>
        _refData.NpcsByInternalName.TryGetValue(internalName, out var npc) && !string.IsNullOrEmpty(npc.Name)
            ? npc.Name!
            : StripNpcPrefix(internalName);

    /// <summary>
    /// Quest InternalNames have no consistent prefix ("KillSkeletons", "GetCatEyeballs"
    /// — not "Quest_..."), so the InternalName is already reasonable to display when the
    /// POCO's Name field is missing. No prefix stripping needed.
    /// </summary>
    private string ResolveQuest(string internalName) =>
        _refData.QuestsByInternalName.TryGetValue(internalName, out var quest) && !string.IsNullOrEmpty(quest.Name)
            ? quest.Name!
            : internalName;

    /// <summary>
    /// Ability InternalNames are bare ASCII identifiers (e.g. "Sword1", "Mentalism5") — no
    /// envelope-key prefix to strip when the POCO's Name is missing.
    /// </summary>
    private string ResolveAbility(string internalName) =>
        _refData.AbilitiesByInternalName.TryGetValue(internalName, out var ability) && !string.IsNullOrEmpty(ability.Name)
            ? ability.Name!
            : internalName;

    /// <summary>
    /// Effect InternalNames are the envelope keys (e.g. <c>"effect_10003"</c>, lifted by
    /// <c>ReferenceDeserializer.ParseEffects</c>). The human-form <see cref="Effect.Name"/>
    /// is preferred for display; the envelope key falls through when no entry is registered
    /// or the Name field is empty (which is uncommon — effects almost always carry a Name).
    /// </summary>
    private string ResolveEffect(string internalName) =>
        _refData.EffectsByInternalName.TryGetValue(internalName, out var effect) && !string.IsNullOrEmpty(effect.Name)
            ? effect.Name!
            : internalName;

    /// <summary>
    /// Skill keys are ASCII identifier-safe (matches <c>[A-Za-z0-9_]+</c>) and frequently
    /// PascalCase (<c>"NonfictionWriting"</c>, <c>"BattleChemistry"</c>). The slim
    /// <see cref="SkillEntry"/> projection carries the human-readable
    /// <see cref="SkillEntry.DisplayName"/>; fall back to the raw key when no entry is
    /// registered (defensive — every PG skill ships with a name).
    /// </summary>
    private string ResolveSkill(string skillKey) =>
        _refData.Skills.TryGetValue(skillKey, out var skill) && !string.IsNullOrEmpty(skill.DisplayName)
            ? skill.DisplayName
            : skillKey;

    private static string StripNpcPrefix(string internalName) =>
        internalName.StartsWith("NPC_", StringComparison.Ordinal)
            ? internalName.Substring(4)
            : internalName;
}
