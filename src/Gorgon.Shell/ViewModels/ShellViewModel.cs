using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Character;
using Gorgon.Shared.Modules;
using Gorgon.Shell.Updates;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Gorgon.Shell.ViewModels;


public sealed partial class ModuleEntry : ObservableObject
{
    public required IGorgonModule Module { get; init; }
    public string Title => Module.DisplayName;
    public PackIconLucideKind Icon => Module.Icon;
    public bool HasImage => !string.IsNullOrEmpty(Module.IconUri);
    public System.Windows.Media.ImageSource? ImageSource =>
        string.IsNullOrEmpty(Module.IconUri) ? null
            : new System.Windows.Media.Imaging.BitmapImage(new Uri(Module.IconUri, UriKind.Absolute));
}

public sealed partial class CharacterChip : ObservableObject
{
    public required string Name { get; init; }
    public required string Server { get; init; }
    public string? ExportLabel { get; init; }

    public string Display => string.IsNullOrEmpty(Server) ? Name : $"{Name} · {Server}";
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ShellSettings _settings;
    private readonly IActiveCharacterService _activeChar;
    private readonly IUpdateStatusService _updateStatus;
    private readonly IUpdateApplier _updateApplier;

    private readonly ModuleGates _gates;

    public ShellViewModel(
        IServiceProvider services,
        IEnumerable<IGorgonModule> modules,
        ShellSettings settings,
        ModuleGates gates,
        IActiveCharacterService activeChar,
        IUpdateStatusService updateStatus,
        IUpdateApplier updateApplier)
    {
        _services = services;
        _settings = settings;
        _gates = gates;
        _activeChar = activeChar;
        _updateStatus = updateStatus;
        _updateApplier = updateApplier;
        foreach (var m in modules.OrderBy(m => m.SortOrder))
            Modules.Add(new ModuleEntry { Module = m });
        var initial = Modules.FirstOrDefault(e => e.Module.Id == settings.ActiveModuleId)
                      ?? Modules.FirstOrDefault();
        if (initial is not null) ActivateModule(initial);

        _activeChar.ActiveCharacterChanged += (_, _) => DispatchRefreshCharacter();
        _activeChar.CharacterExportsChanged += (_, _) => DispatchRefreshCharacter();
        _updateStatus.StateChanged += (_, _) => RefreshUpdateStatus();
        RefreshCharacter();
        RefreshUpdateStatus();
    }

    public ObservableCollection<ModuleEntry> Modules { get; } = new();
    public ObservableCollection<CharacterChip> AvailableCharacters { get; } = new();

    public string VersionText { get; } = BuildVersionText();

    [ObservableProperty] private ModuleEntry? _selectedModule;
    [ObservableProperty] private object? _activeContent;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _activeCharacterName = "";
    [ObservableProperty] private string _activeServer = "";
    [ObservableProperty] private bool _hasActiveCharacter;
    [ObservableProperty] private bool _hasNoCharacters;

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateBannerText = "";

    [RelayCommand]
    private void DismissUpdate() => _updateStatus.Dismiss();

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (!_updateStatus.IsOutdated) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _updateApplier.DownloadAndApplyAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* timeout — surface again next check */ }
    }

    private void RefreshUpdateStatus()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) ApplyUpdateStatus();
        else d.InvokeAsync(ApplyUpdateStatus);
    }

    private void ApplyUpdateStatus()
    {
        IsUpdateAvailable = _updateStatus.IsOutdated;
        var v = _updateStatus.RemoteVersion;
        UpdateBannerText = string.IsNullOrEmpty(v)
            ? "An update is available — click Install to download and restart."
            : $"Gorgon v{v} is available — click Install to download and restart.";
    }

    [RelayCommand]
    private void SelectCharacter(CharacterChip? chip)
    {
        if (chip is null) return;
        _activeChar.SetActiveCharacter(chip.Name, chip.Server);
    }

    private void DispatchRefreshCharacter()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) RefreshCharacter();
        else d.InvokeAsync(RefreshCharacter);
    }

    private void RefreshCharacter()
    {
        ActiveCharacterName = _activeChar.ActiveCharacterName ?? "";
        ActiveServer = _activeChar.ActiveServer ?? "";
        HasActiveCharacter = !string.IsNullOrEmpty(ActiveCharacterName);

        AvailableCharacters.Clear();
        foreach (var c in _activeChar.Characters)
        {
            AvailableCharacters.Add(new CharacterChip
            {
                Name = c.Name,
                Server = c.Server,
                ExportLabel = c.ExportedAt == default ? null : c.ExportedAt.LocalDateTime.ToString("MMM d, HH:mm"),
            });
        }
        HasNoCharacters = AvailableCharacters.Count == 0;
    }

    private static string BuildVersionText()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return "v" + (asm.GetName().Version?.ToString(3) ?? "?");
        var plus = info.IndexOf('+');
        return "v" + (plus >= 0 ? info[..plus] : info);
    }

    partial void OnSelectedModuleChanged(ModuleEntry? value)
    {
        if (value is not null) ActivateModule(value);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SelectedModule = null;
        ActiveContent = _services.GetRequiredService<Views.SettingsHostView>();
        StatusText = "Settings";
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
