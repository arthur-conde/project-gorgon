namespace Mithril.Reference.Models.Sources;

/// <summary>
/// Polymorphic entry inside a sources_*.json envelope, describing one place
/// the keyed thing (item, recipe, or ability) can be obtained. Shared across
/// <c>sources_items.json</c>, <c>sources_recipes.json</c>, and
/// <c>sources_abilities.json</c> — the entry-type shapes (field sets per
/// discriminator) are identical across all three files even when the set of
/// <i>which</i> types appear differs by file.
/// </summary>
/// <remarks>
/// Discriminator field is <c>type</c> (lowercase) and field names are
/// camelCase — different convention from the rest of BundledData but
/// consistent within the sources_*.json family. POCO property names match
/// the JSON exactly to avoid per-type contract resolvers.
/// </remarks>
public abstract class SourceEntry
{
    public string type { get; set; } = "";
}

/// <summary>Sentinel for any <c>type</c> value not covered by a concrete subclass.</summary>
public sealed class UnknownSourceEntry : SourceEntry, IUnknownDiscriminator
{
    public string DiscriminatorValue { get; set; } = "";
}

public sealed class AnglingSource : SourceEntry { }

public sealed class BarterSource : SourceEntry
{
    public string? npc { get; set; }
}

public sealed class CorpseButcheringSource : SourceEntry { }

public sealed class CorpseSkinningSource : SourceEntry { }

public sealed class CorpseSkullExtractionSource : SourceEntry { }

public sealed class CraftedInteractorSource : SourceEntry
{
    public string? friendlyName { get; set; }
}

public sealed class EffectSource : SourceEntry { }

public sealed class HangOutSource : SourceEntry
{
    public string? npc { get; set; }
    public int hangOutId { get; set; }
}

public sealed class ItemSource : SourceEntry
{
    public long itemTypeId { get; set; }
}

public sealed class MonsterSource : SourceEntry { }

public sealed class NpcGiftSource : SourceEntry
{
    public string? npc { get; set; }
}

public sealed class QuestSource : SourceEntry
{
    public long questId { get; set; }
}

public sealed class QuestObjectiveMacGuffinSource : SourceEntry
{
    public long questId { get; set; }
}

public sealed class RecipeSource : SourceEntry
{
    public long recipeId { get; set; }
}

public sealed class ResourceInteractorSource : SourceEntry
{
    public string? friendlyName { get; set; }
}

public sealed class SkillSource : SourceEntry
{
    public string? skill { get; set; }
}

public sealed class TrainingSource : SourceEntry
{
    public string? npc { get; set; }
}

public sealed class TreasureMapSource : SourceEntry { }

public sealed class VendorSource : SourceEntry
{
    public string? npc { get; set; }
}
