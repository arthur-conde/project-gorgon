using System.Collections.ObjectModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Gorgon.Shell.ViewModels;

public sealed partial class ModuleEntry : ObservableObject
{
    public required IGorgonModule Module { get; init; }
    public string Title => Module.DisplayName;
    public string Icon => Module.Icon;
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ShellSettings _settings;

    public ShellViewModel(IServiceProvider services, IEnumerable<IGorgonModule> modules, ShellSettings settings)
    {
        _services = services;
        _settings = settings;
        foreach (var m in modules.OrderBy(m => m.SortOrder))
            Modules.Add(new ModuleEntry { Module = m });
        var initial = Modules.FirstOrDefault(e => e.Module.Id == settings.ActiveModuleId)
                      ?? Modules.FirstOrDefault();
        if (initial is not null) ActivateModule(initial);
    }

    public ObservableCollection<ModuleEntry> Modules { get; } = new();

    [ObservableProperty] private ModuleEntry? _selectedModule;
    [ObservableProperty] private object? _activeContent;
    [ObservableProperty] private string _statusText = "";

    partial void OnSelectedModuleChanged(ModuleEntry? value)
    {
        if (value is not null) ActivateModule(value);
    }

    [RelayCommand]
    private void OpenGameConfig()
    {
        ActiveContent = _services.GetRequiredService<Views.GameConfigView>();
        StatusText = "Game configuration";
    }

    [RelayCommand]
    private void OpenHotkeys()
    {
        ActiveContent = _services.GetRequiredService<Views.HotkeyBindingsView>();
        StatusText = "Hotkeys";
    }

    private void ActivateModule(ModuleEntry entry)
    {
        SelectedModule = entry;
        var view = (Control)_services.GetRequiredService(entry.Module.ViewType);
        ActiveContent = view;
        _settings.ActiveModuleId = entry.Module.Id;
        StatusText = entry.Module.DisplayName;
    }
}
