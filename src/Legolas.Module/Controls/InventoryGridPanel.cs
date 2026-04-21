using System.Windows;
using System.Windows.Controls;

namespace Legolas.Controls;

/// <summary>
/// Lays out children in a fixed-column grid with independently configurable
/// cell width, cell height, column gap, and row gap. Children are arranged
/// row-major; each child is given exactly (CellWidth x CellHeight).
/// </summary>
public sealed class InventoryGridPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(InventoryGridPanel),
        new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(
        nameof(CellWidth), typeof(double), typeof(InventoryGridPanel),
        new FrameworkPropertyMetadata(50.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(
        nameof(CellHeight), typeof(double), typeof(InventoryGridPanel),
        new FrameworkPropertyMetadata(50.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(InventoryGridPanel),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(InventoryGridPanel),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public double CellHeight
    {
        get => (double)GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = Math.Max(1, Columns);
        var cellSize = new Size(CellWidth, CellHeight);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(cellSize);
        }
        var rows = (InternalChildren.Count + cols - 1) / cols;
        var width = cols * CellWidth + Math.Max(0, cols - 1) * ColumnGap;
        var height = rows * CellHeight + Math.Max(0, rows - 1) * RowGap;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = Math.Max(1, Columns);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var col = i % cols;
            var row = i / cols;
            var x = col * (CellWidth + ColumnGap);
            var y = row * (CellHeight + RowGap);
            child.Arrange(new Rect(x, y, CellWidth, CellHeight));
        }
        return finalSize;
    }
}
