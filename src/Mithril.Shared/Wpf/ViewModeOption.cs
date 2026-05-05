using System.Windows;
using System.Windows.Markup;

namespace Mithril.Shared.Wpf;

/// <summary>
/// One segment in a <see cref="ViewModeToggle"/>. <see cref="Id"/> is the value reflected in
/// <see cref="ViewModeToggle.SelectedMode"/>; <see cref="Icon"/> is rendered through a
/// <see cref="System.Windows.Controls.ContentPresenter"/> so consumers can pass any
/// <c>FrameworkElement</c> (e.g. a <c>PackIconLucide</c>).
/// </summary>
[ContentProperty(nameof(Icon))]
public sealed class ViewModeOption : DependencyObject
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.Register(
        nameof(Id), typeof(string), typeof(ViewModeOption), new PropertyMetadata(""));

    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(ViewModeOption), new PropertyMetadata(""));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(object), typeof(ViewModeOption));

    public static readonly DependencyProperty ToolTipTextProperty = DependencyProperty.Register(
        nameof(ToolTipText), typeof(string), typeof(ViewModeOption), new PropertyMetadata(""));

    public string Id
    {
        get => (string)GetValue(IdProperty);
        set => SetValue(IdProperty, value);
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? ToolTipText
    {
        get => (string?)GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }
}
