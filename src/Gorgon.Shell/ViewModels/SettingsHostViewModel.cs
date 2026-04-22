using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Modules;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Gorgon.Shell.ViewModels;

public sealed partial class SettingsSection : ObservableObject
{
    public required string Title { get; init; }
    public required PackIconLucideKind Icon { get; init; }
    public required Type ViewType { get; init; }
    public string? IconUri { get; init; }
    public bool HasImage => !string.IsNullOrEmpty(IconUri);
    public System.Windows.Media.ImageSource? ImageSource =>
        string.IsNullOrEmpty(IconUri) ? null
            : new System.Windows.Media.Imaging.BitmapImage(new Uri(IconUri, UriKind.Absolute));
}

public sealed partial class SettingsHostViewModel : ObservableObject
{
    private readonly IServiceProvider _services;

    public SettingsHostViewModel(IServiceProvider services, IEnumerable<IGorgonModule> modules)
    {
        _services = services;

        // Shell-owned sections always come first.
        Sections.Add(new SettingsSection
        {
            Title = "Game folder",
            Icon = PackIconLucideKind.FolderOpen,
            ViewType = typeof(Views.GameConfigView),
            IconUri = "pack://application:,,,/Gorgon;component/Resources/game-folder.ico",
        });
        Sections.Add(new SettingsSection
        {
            Title = "Reference data",
            Icon = PackIconLucideKind.Package,
            ViewType = typeof(Views.ReferenceDataView),
            IconUri = "pack://application:,,,/Gorgon;component/Resources/reference-data.ico",
        });
        Sections.Add(new SettingsSection
        {
            Title = "Icons",
            Icon = PackIconLucideKind.Image,
            ViewType = typeof(Views.IconSettingsView),
            IconUri = "pack://application:,,,/Gorgon;component/Resources/icons.ico",
        });

        foreach (var m in modules.OrderBy(m => m.SortOrder))
        {
            if (m.SettingsViewType is null) continue;
            Sections.Add(new SettingsSection
            {
                Title = m.DisplayName,
                Icon = m.Icon,
                ViewType = m.SettingsViewType,
                IconUri = m.IconUri,
            });
        }

        SelectedSection = Sections.FirstOrDefault();
    }

    public ObservableCollection<SettingsSection> Sections { get; } = new();

    [ObservableProperty] private SettingsSection? _selectedSection;
    [ObservableProperty] private object? _activeContent;

    partial void OnSelectedSectionChanged(SettingsSection? value)
    {
        ActiveContent = value is null ? null : _services.GetRequiredService(value.ViewType);
    }
}
