using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// A subsystem grouping of <see cref="TagChip"/> entries in the telemetry tag cloud.
/// The grouping mirrors the producer subsystems declared on each
/// <see cref="Mithril.Shared.Telemetry.Abstractions.TagDescriptor"/>.
/// </summary>
public sealed class TagChipGroup
{
    public string Subsystem { get; }
    public ObservableCollection<TagChip> Chips { get; }

    public TagChipGroup(string subsystem, IEnumerable<TagChip> chips)
    {
        Subsystem = subsystem;
        Chips = new ObservableCollection<TagChip>(chips);
    }
}
