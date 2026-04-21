using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Gorgon.Shared.Wpf;

/// <summary>
/// Reusable cell control that renders an item/recipe icon beside a display name.
/// Use in DataGrid template columns to replace separate icon + name columns.
/// </summary>
public sealed class IconNameCell : Control
{
    public static readonly DependencyProperty IconIdProperty = DependencyProperty.Register(
        nameof(IconId), typeof(int), typeof(IconNameCell),
        new FrameworkPropertyMetadata(0));

    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(IconNameCell),
        new FrameworkPropertyMetadata(string.Empty));

    public int IconId
    {
        get => (int)GetValue(IconIdProperty);
        set => SetValue(IconIdProperty, value);
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    private readonly IconImage _icon;
    private readonly TextBlock _text;
    private readonly StackPanel _panel;

    public IconNameCell()
    {
        _icon = new IconImage { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        _text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 0, 0),
        };
        _panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 2, 4, 2),
        };
        _panel.Children.Add(_icon);
        _panel.Children.Add(_text);

        AddVisualChild(_panel);
        AddLogicalChild(_panel);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IconIdProperty)
            _icon.IconId = (int)e.NewValue;
        else if (e.Property == DisplayNameProperty)
            _text.Text = (string)e.NewValue ?? "";
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _panel;

    protected override Size MeasureOverride(Size constraint)
    {
        _panel.Measure(constraint);
        return _panel.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _panel.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
