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
    public bool HasImage => !string.IsNullOrEmpty(Module.IconUri);
    public System.Windows.Media.ImageSource? ImageSource =>
        string.IsNullOrEmpty(Module.IconUri) ? null
            : new System.Windows.Media.Imaging.BitmapImage(new Uri(Module.IconUri, UriKind.Absolute));
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ShellSettings _settings;

    private readonly ModuleGates _gates;

    public ShellViewModel(IServiceProvider services, IEnumerable<IGorgonModule> modules, ShellSettings settings, ModuleGates gates)
    {
        _services = services;
        _settings = settings;
        _gates = gates;
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
        SelectedModule = null; // so re-selecting a module fires the change event
        ActiveContent = _services.GetRequiredService<Views.GameConfigView>();
        StatusText = "Game configuration";
    }

    [RelayCommand]
    private void OpenHotkeys()
    {
        SelectedModule = null;
        ActiveContent = _services.GetRequiredService<Views.HotkeyBindingsView>();
        StatusText = "Hotkeys";
    }

    [RelayCommand]
    private void OpenDiagnostics()
    {
        SelectedModule = null;
        ActiveContent = _services.GetRequiredService<Views.DiagnosticsView>();
        StatusText = "Diagnostics";
    }

    private void ActivateModule(ModuleEntry entry)
    {
        SelectedModule = entry;
        _gates.For(entry.Module.Id).Open();          // Lazy modules wake up here
        var view = (System.Windows.Controls.Control)_services.GetRequiredService(entry.Module.ViewType);
        ActiveContent = view;
        _settings.ActiveModuleId = entry.Module.Id;
        StatusText = entry.Module.DisplayName;
    }
}
