using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Mithril.Shared.Modules;
using Mithril.Shell.ViewModels;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Mithril.Shell.Views;

public partial class ShellWindow : System.Windows.Window
{
    private readonly IAttentionAggregator _attention;
    private readonly Icon _plainIcon;
    private readonly Icon _attentionIcon;

    public ShellWindow(IAttentionAggregator attention)
    {
        _attention = attention;
        InitializeComponent();

        _plainIcon = LoadIcon("mithril.ico") ?? SystemIcons.Application;
        _attentionIcon = LoadIcon("mithril-attention.ico") ?? _plainIcon;

        Tray.TrayLeftMouseDown += (_, _) =>
        {
            Show();
            WindowState = System.Windows.WindowState.Normal;
            Activate();
        };

        _attention.PropertyChanged += OnAttentionPropertyChanged;
        RefreshTray();
    }

    private void OnAttentionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAttentionAggregator.TotalCount)
            or nameof(IAttentionAggregator.HasAttention)
            or nameof(IAttentionAggregator.Entries))
            RefreshTray();
    }

    private void RefreshTray()
    {
        var total = _attention.TotalCount;
        Tray.Icon = total > 0 ? _attentionIcon : _plainIcon;
        Tray.ToolTipText = total switch
        {
            <= 0 => "Mithril",
            1 => "Mithril — 1 item needs attention",
            _ => $"Mithril — {total} items need attention",
        };
        RebuildAttentionMenuItems();
    }

    private void RebuildAttentionMenuItems()
    {
        var topIdx = TrayMenu.Items.IndexOf(TrayAttentionSeparatorTop);
        var bottomIdx = TrayMenu.Items.IndexOf(TrayAttentionSeparatorBottom);
        if (topIdx < 0 || bottomIdx < 0 || bottomIdx <= topIdx) return;

        for (var i = bottomIdx - 1; i > topIdx; i--)
            TrayMenu.Items.RemoveAt(i);

        var withCount = 0;
        foreach (var entry in _attention.Entries)
        {
            if (entry.Count <= 0) continue;
            var item = new MenuItem
            {
                Header = $"{entry.DisplayLabel} ({entry.Count})",
                Tag = entry.ModuleId,
            };
            item.Click += OnAttentionMenuItemClick;
            TrayMenu.Items.Insert(bottomIdx + withCount, item);
            withCount++;
        }

        TrayAttentionSeparatorTop.Visibility = withCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayAttentionSeparatorBottom.Visibility = withCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAttentionMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string moduleId) return;
        if (DataContext is not ShellViewModel vm) return;
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
        vm.ActivateModuleByIdCommand.Execute(moduleId);
    }

    private void OnShowFromTray(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private void OnExitFromTray(object sender, RoutedEventArgs e)
        => System.Windows.Application.Current.Shutdown();

    private static Icon? LoadIcon(string fileName)
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri($"pack://application:,,,/Resources/{fileName}", UriKind.Absolute));
            if (info?.Stream is { } s) using (s) return new Icon(s);
        }
        catch { }
        return null;
    }
}
