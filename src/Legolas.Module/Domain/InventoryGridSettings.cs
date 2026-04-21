using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Legolas.Domain;

/// <summary>
/// Inventory overlay grid configuration. Manual <see cref="INotifyPropertyChanged"/>
/// (instead of <c>[ObservableProperty]</c>) because the CommunityToolkit.Mvvm
/// source generator + System.Text.Json source generator race: STJ can fail to
/// emit serialization for the generated partial properties, leaving them out of
/// settings.json. Plain auto-properties avoid that interaction.
/// </summary>
public sealed class InventoryGridSettings : INotifyPropertyChanged
{
    private int _columns = 10;
    public int Columns
    {
        get => _columns;
        set => SetClampedInt(ref _columns, value, 1, 20);
    }

    private double _cellWidth = 50;
    public double CellWidth
    {
        get => _cellWidth;
        set
        {
            if (SetClamped(ref _cellWidth, value, 20.0, 200.0))
                Raise(nameof(SlotStrideX));
        }
    }

    private double _cellHeight = 50;
    public double CellHeight
    {
        get => _cellHeight;
        set
        {
            if (SetClamped(ref _cellHeight, value, 20.0, 200.0))
                Raise(nameof(SlotStrideY));
        }
    }

    private double _columnGap = 2;
    public double ColumnGap
    {
        get => _columnGap;
        set
        {
            if (SetClamped(ref _columnGap, value, 0.0, 20.0))
                Raise(nameof(SlotStrideX));
        }
    }

    private double _rowGap = 2;
    public double RowGap
    {
        get => _rowGap;
        set
        {
            if (SetClamped(ref _rowGap, value, 0.0, 20.0))
                Raise(nameof(SlotStrideY));
        }
    }

    [JsonIgnore] public double SlotStrideX => CellWidth + ColumnGap;
    [JsonIgnore] public double SlotStrideY => CellHeight + RowGap;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetClamped(ref double field, double value, double min, double max, [CallerMemberName] string? name = null)
    {
        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(field - clamped) < 1e-6) return false;
        field = clamped;
        Raise(name);
        return true;
    }

    private void SetClampedInt(ref int field, int value, int min, int max, [CallerMemberName] string? name = null)
    {
        var clamped = Math.Clamp(value, min, max);
        if (field == clamped) return;
        field = clamped;
        Raise(name);
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
