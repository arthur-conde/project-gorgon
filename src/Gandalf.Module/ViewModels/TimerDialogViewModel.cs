using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gandalf.Domain;
using Gorgon.Shared.Wpf.Dialogs;

namespace Gandalf.ViewModels;

public sealed partial class TimerDialogViewModel : DialogViewModelBase
{
    private readonly bool _isEditing;
    private readonly bool _isDurationEditable;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _hours = "";
    [ObservableProperty] private string _minutes = "";
    [ObservableProperty] private string _seconds = "";
    [ObservableProperty] private string _region = "";
    [ObservableProperty] private string _map = "";

    public ObservableCollection<string> KnownRegions { get; } = [];
    public ObservableCollection<string> KnownMaps { get; } = [];

    public bool IsDurationEditable => _isDurationEditable;

    public override string Title => _isEditing ? "Edit Timer" : "Add Timer";
    public override string PrimaryButtonText => _isEditing ? "Update" : "Save";

    public TimerDialogViewModel(
        TimerView? existing,
        IReadOnlyList<string> knownRegions,
        IReadOnlyList<string> knownMaps)
    {
        foreach (var r in knownRegions) KnownRegions.Add(r);
        foreach (var m in knownMaps) KnownMaps.Add(m);

        if (existing is not null)
        {
            _isEditing = true;
            // Duration is editable only when the active character's progress is idle —
            // changing Duration mid-run would reinterpret remaining time. Other characters
            // with in-flight progress for the same def accept the new duration on their
            // next render (rare corner case; users can Restart).
            _isDurationEditable = existing.State == TimerState.Idle;
            _name = existing.Def.Name;
            _hours = existing.Def.Duration.Hours > 0 || existing.Def.Duration.Days > 0
                ? ((int)existing.Def.Duration.TotalHours).ToString() : "";
            _minutes = existing.Def.Duration.Minutes > 0 ? existing.Def.Duration.Minutes.ToString() : "";
            _seconds = existing.Def.Duration.Seconds > 0 ? existing.Def.Duration.Seconds.ToString() : "";
            _region = existing.Def.Region;
            _map = existing.Def.Map;
        }
        else
        {
            _isDurationEditable = true;
        }
    }

    public override bool OnPrimaryAction()
    {
        if (!_isEditing)
        {
            int.TryParse(Hours, out var h);
            int.TryParse(Minutes, out var m);
            int.TryParse(Seconds, out var s);
            if (new TimeSpan(h, m, s) <= TimeSpan.Zero)
                return false;
        }

        return true;
    }

    public string ResultName => string.IsNullOrWhiteSpace(Name) ? "Timer" : Name.Trim();

    public TimeSpan ResultDuration
    {
        get
        {
            int.TryParse(Hours, out var h);
            int.TryParse(Minutes, out var m);
            int.TryParse(Seconds, out var s);
            return new TimeSpan(h, m, s);
        }
    }

    public string ResultRegion => Region.Trim();
    public string ResultMap => Map.Trim();

    partial void OnRegionChanged(string value)
    {
        KnownMaps.Clear();
        // Maps will be filtered by the caller if needed — keep it simple for now.
    }
}
