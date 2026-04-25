using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Mithril.Shared.Wpf;

/// <summary>Fixed-cell virtualizing wrap panel. Assumes every item measures to <see cref="ItemWidth"/> × <see cref="ItemHeight"/>.</summary>
public class MithrilVirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(MithrilVirtualizingWrapPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(MithrilVirtualizingWrapPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private ScrollViewer? _owner;
    private int _firstVisibleIndex;

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count ?? 0;
        var itemWidth = ItemWidth;
        var itemHeight = ItemHeight;

        var viewportWidth = double.IsInfinity(availableSize.Width) ? itemWidth : availableSize.Width;
        var itemsPerRow = Math.Max(1, (int)Math.Floor(viewportWidth / itemWidth));
        var rowCount = itemCount == 0 ? 0 : (int)Math.Ceiling((double)itemCount / itemsPerRow);
        var totalHeight = rowCount * itemHeight;

        // When hosted without a ScrollViewer (e.g. inside a grouped GroupItem), the panel
        // receives infinite vertical space. Fall back to rendering all items at natural extent.
        var viewportHeight = double.IsInfinity(availableSize.Height) ? totalHeight : availableSize.Height;

        var extent = new Size(itemsPerRow * itemWidth, totalHeight);
        var viewport = new Size(viewportWidth, viewportHeight);
        UpdateScrollInfo(extent, viewport);

        var firstRow = (int)Math.Floor(_offset.Y / itemHeight);
        var lastRow = (int)Math.Ceiling((_offset.Y + viewportHeight) / itemHeight);
        var firstIndex = Math.Max(0, firstRow * itemsPerRow);
        var lastIndex = Math.Min(itemCount - 1, lastRow * itemsPerRow + itemsPerRow - 1);
        _firstVisibleIndex = firstIndex;

        var returnSize = _owner == null
            ? new Size(viewportWidth, totalHeight)
            : viewport;

        var generator = ItemContainerGenerator;
        if (generator == null || itemCount == 0)
        {
            CleanUpItems(0, -1);
            return returnSize;
        }

        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        var childSize = new Size(itemWidth, itemHeight);
        var generatedThrough = firstIndex - 1;
        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var i = firstIndex; i <= lastIndex; i++, childIndex++)
            {
                if (generator.GenerateNext(out var isNew) is not UIElement child) break;
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(childSize);
                generatedThrough = i;
            }
        }

        CleanUpItems(firstIndex, generatedThrough);
        return returnSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count ?? 0;
        var itemWidth = ItemWidth;
        var itemHeight = ItemHeight;
        var itemsPerRow = Math.Max(1, (int)Math.Floor(finalSize.Width / itemWidth));

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var itemIndex = _firstVisibleIndex + i;
            if (itemIndex >= itemCount) break;

            var row = itemIndex / itemsPerRow;
            var col = itemIndex % itemsPerRow;
            var rect = new Rect(col * itemWidth - _offset.X, row * itemHeight - _offset.Y, itemWidth, itemHeight);
            child.Arrange(rect);
        }

        return finalSize;
    }

    private void CleanUpItems(int minIndex, int maxIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator == null) return;

        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var childPos = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(childPos);
            if (itemIndex < minIndex || itemIndex > maxIndex)
            {
                generator.Remove(childPos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }
        InvalidateMeasure();
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = false;
        if (extent != _extent) { _extent = extent; changed = true; }
        if (viewport != _viewport) { _viewport = viewport; changed = true; }

        var maxY = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > maxY) { _offset.Y = maxY; changed = true; }
        if (_offset.Y < 0) { _offset.Y = 0; changed = true; }

        if (changed) _owner?.InvalidateScrollInfo();
    }

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner
    {
        get => _owner;
        set => _owner = value;
    }

    private const double LineDelta = 16.0;

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineDelta);
    public void LineDown() => SetVerticalOffset(VerticalOffset + LineDelta);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - LineDelta * 3);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + LineDelta * 3);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() { }
    public void PageRight() { }

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Max(0, Math.Min(offset, max));
        if (Math.Abs(offset - _offset.Y) < 0.0001) return;
        _offset.Y = offset;
        _owner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual)) continue;
            var itemIndex = _firstVisibleIndex + i;
            var itemsPerRow = Math.Max(1, (int)Math.Floor(_viewport.Width / ItemWidth));
            var row = itemIndex / itemsPerRow;
            var top = row * ItemHeight;
            var bottom = top + ItemHeight;
            if (top < _offset.Y) SetVerticalOffset(top);
            else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
            return new Rect(0, top - _offset.Y, ItemWidth, ItemHeight);
        }
        return Rect.Empty;
    }
}
