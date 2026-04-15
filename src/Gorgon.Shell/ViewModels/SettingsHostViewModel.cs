using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Gorgon.Shell.ViewModels;

public sealed partial class SettingsSection : ObservableObject
{
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required Type ViewType { get; init; }
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
            Icon = "📁",
            ViewType = typeof(Views.GameConfigView),
        });

        foreach (var m in modules.OrderBy(m => m.SortOrder))
        {
            if (m.SettingsViewType is null) continue;
            Sections.Add(new SettingsSection
            {
                Title = m.DisplayName,
                Icon = m.Icon,
                ViewType = m.SettingsViewType,
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
