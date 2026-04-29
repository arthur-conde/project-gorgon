using System;
using System.Collections.Generic;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Serialization.Converters;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// Maps the JSON <c>Type</c> discriminator strings to concrete
/// <see cref="NpcService"/> subclasses for the <c>Services</c> field on NPCs.
/// </summary>
internal static class NpcDiscriminators
{
    public static DiscriminatedUnionConverter<NpcService, UnknownNpcService>
        BuildServiceConverter()
        => new("Type", ServiceMap);

    private static readonly IReadOnlyDictionary<string, Type> ServiceMap = new Dictionary<string, Type>
    {
        ["AnimalHusbandry"] = typeof(AnimalHusbandryService),
        ["Barter"] = typeof(BarterService),
        ["Consignment"] = typeof(ConsignmentService),
        ["GuildQuests"] = typeof(GuildQuestsService),
        ["InstallAugments"] = typeof(InstallAugmentsService),
        ["Stables"] = typeof(StablesService),
        ["Storage"] = typeof(StorageService),
        ["Store"] = typeof(StoreService),
        ["Training"] = typeof(TrainingService),
    };
}
