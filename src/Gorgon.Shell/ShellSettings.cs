using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Gorgon.Shared.Hotkeys;

namespace Gorgon.Shell;

public sealed class ShellSettings : INotifyPropertyChanged
{
    private string _gameRoot = "";
    public string GameRoot { get => _gameRoot; set => Set(ref _gameRoot, value); }

    private string _activeModuleId = "";
    public string ActiveModuleId { get => _activeModuleId; set => Set(ref _activeModuleId, value); }

    private bool _concurrentAlarms;
    public bool ConcurrentAlarms { get => _concurrentAlarms; set => Set(ref _concurrentAlarms, value); }

    private double _windowLeft = 200, _windowTop = 200, _windowWidth = 1100, _windowHeight = 700;
    public double WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }
    public double WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }

    public Dictionary<string, HotkeyBinding> HotkeyBindings { get; set; } = new();
    public Dictionary<string, bool> ModuleEagerOverrides { get; set; } = new();

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
