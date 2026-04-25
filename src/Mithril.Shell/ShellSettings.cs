using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;
using Mithril.Shared.Hotkeys;

namespace Mithril.Shell;

public sealed class ShellSettings : INotifyPropertyChanged, IActiveCharacterPersistence
{
    private string _gameRoot = "";
    public string GameRoot { get => _gameRoot; set => Set(ref _gameRoot, value); }

    private string _activeModuleId = "";
    public string ActiveModuleId { get => _activeModuleId; set => Set(ref _activeModuleId, value); }

    private string? _activeCharacterName;
    public string? ActiveCharacterName { get => _activeCharacterName; set => Set(ref _activeCharacterName, value); }

    private string? _activeServer;
    public string? ActiveServer { get => _activeServer; set => Set(ref _activeServer, value); }

    private bool _concurrentAlarms;
    public bool ConcurrentAlarms { get => _concurrentAlarms; set => Set(ref _concurrentAlarms, value); }

    private bool _developerMode;
    public bool DeveloperMode { get => _developerMode; set => Set(ref _developerMode, value); }

    private string _uiFontFamily = "Segoe UI";
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, value); }

    private double _uiFontSize = 12.0;
    public double UiFontSize { get => _uiFontSize; set => Set(ref _uiFontSize, value); }

    private double _windowLeft = 200, _windowTop = 200, _windowWidth = 1100, _windowHeight = 700;
    public double WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }
    public double WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }

    public Dictionary<string, HotkeyBinding> HotkeyBindings { get; set; } = new();
    public Dictionary<string, bool> ModuleEagerOverrides { get; set; } = new();

    private string? _lastDismissedUpdateVersion;
    public string? LastDismissedUpdateVersion { get => _lastDismissedUpdateVersion; set => Set(ref _lastDismissedUpdateVersion, value); }

    private double _updateCheckIntervalHours = 4.0;
    public double UpdateCheckIntervalHours { get => _updateCheckIntervalHours; set => Set(ref _updateCheckIntervalHours, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ShellSettings))]
public partial class ShellSettingsJsonContext : JsonSerializerContext { }
