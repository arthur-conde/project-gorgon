namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Searchable list of all landmarks + NPCs in the current area. Selection
/// drives "click the source map to place a ref at the highlighted entry's
/// world coord" — set by the user, cleared by
/// <see cref="AreaWorkspaceViewModel.PlaceRefAt"/> after the click.
///
/// <para>NPCs and landmarks both come through <c>LandmarksReader</c> /
/// <c>NpcsReader</c> as <see cref="LandmarkRef"/> records — the picker only
/// needs name + world coord, plus a kind chip ("Npc" vs landmark Type) for
/// disambiguation when names clash.</para>
/// </summary>
public sealed partial class LandmarkPickerViewModel : ObservableObject
{
    private readonly List<LandmarkPickerItem> _all;

    public IReadOnlyList<LandmarkPickerItem> AllItems => _all;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private LandmarkPickerItem? _selected;

    public ObservableCollection<LandmarkPickerItem> Filtered { get; } = new();

    public LandmarkPickerViewModel(string area, string landmarksJsonPath, string npcsJsonPath)
    {
        var landmarks = LandmarksReader.LoadForArea(landmarksJsonPath, area);
        var npcs = NpcsReader.LoadForArea(npcsJsonPath, area);
        _all = [
            .. landmarks.Select(l => new LandmarkPickerItem(l.Name, l.Type, l.World)),
            .. npcs.Select(n => new LandmarkPickerItem(n.Name, n.Type, n.World)),
        ];
        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Refilter();
    }

    partial void OnSearchTextChanged(string value) => Refilter();

    private void Refilter()
    {
        Filtered.Clear();
        var q = SearchText.Trim();
        foreach (var item in _all)
        {
            if (q.Length == 0 || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                Filtered.Add(item);
            }
        }
    }
}

/// <summary>
/// One row in the picker: display name, the source kind (landmark Type or
/// "Npc"), and the world coord that gets paired with the user's click pixel.
/// </summary>
public sealed record LandmarkPickerItem(string Name, string Kind, WorldCoord World);
