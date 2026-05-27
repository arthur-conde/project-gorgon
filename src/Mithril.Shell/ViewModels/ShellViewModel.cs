using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Arda.Contracts.State.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Game;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Mithril.Shell.Updates;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Mithril.Shell.ViewModels;


public sealed partial class ModuleEntry : ObservableObject
{
    public required IMithrilModule Module { get; init; }
    public string Title => Module.DisplayName;
    public PackIconLucideKind Icon => Module.Icon;
    public bool HasImage => !string.IsNullOrEmpty(Module.IconUri);
    public System.Windows.Media.ImageSource? ImageSource =>
        string.IsNullOrEmpty(Module.IconUri) ? null
            : new System.Windows.Media.Imaging.BitmapImage(new Uri(Module.IconUri, UriKind.Absolute));

    [ObservableProperty]
    private int _attentionCount;

    public bool HasAttention => AttentionCount > 0;

    partial void OnAttentionCountChanged(int value) => OnPropertyChanged(nameof(HasAttention));
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
    private readonly UserPreferences _preferences;
    private readonly IActiveCharacterService _activeChar;
    private readonly IUpdateStatusService _updateStatus;
    private readonly IUpdateApplier _updateApplier;
    private readonly IAttentionAggregator _attention;
    private readonly IGameClock _gameClock;
    private readonly IShiftCatalog _shiftCatalog;
    private readonly IReadOnlyList<IMithrilModule> _allModules;

    private readonly ModuleGates _gates;
    private readonly DispatcherTimer _gameClockTimer;
    private readonly IPerfTracer _perf;
    private readonly IWorldHealthView _health;

    public ShellViewModel(
        IServiceProvider services,
        IEnumerable<IMithrilModule> modules,
        ShellSettings settings,
        UserPreferences preferences,
        ModuleGates gates,
        IActiveCharacterService activeChar,
        IUpdateStatusService updateStatus,
        IUpdateApplier updateApplier,
        IAttentionAggregator attention,
        IGameClock gameClock,
        IShiftCatalog shiftCatalog,
        IPerfTracer perf,
        IWorldHealthView health)
    {
        _services = services;
        _settings = settings;
        _preferences = preferences;
        _gates = gates;
        _activeChar = activeChar;
        _updateStatus = updateStatus;
        _updateApplier = updateApplier;
        _attention = attention;
        _gameClock = gameClock;
        _shiftCatalog = shiftCatalog;
        _perf = perf;
        _health = health;
        _allModules = modules.OrderBy(m => m.SortOrder).ToList();
        RebuildVisibleModules();

        _activeChar.ActiveCharacterChanged += (_, _) => DispatchRefreshCharacter();
        _activeChar.CharacterExportsChanged += (_, _) => DispatchRefreshCharacter();
        _updateStatus.StateChanged += (_, _) => RefreshUpdateStatus();
        _settings.PropertyChanged += OnSettingsChanged;
        _preferences.PropertyChanged += OnPreferencesChanged;
        _attention.AttentionChanged += OnAttentionChanged;
        _perf.IsActiveChanged += OnPerfTraceStateChanged;
        _health.Changed += OnHealthChanged;
        RefreshPerfTraceState();
        RefreshHealthState();
        RefreshCharacter();
        RefreshUpdateStatus();
        RefreshAttentionSurface();

        // 5 real seconds = 1 in-game minute (the smallest unit we display).
        RefreshGameTime();
        _gameClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _gameClockTimer.Tick += (_, _) => RefreshGameTime();
        _gameClockTimer.Start();
    }

    private bool _initialized;

    /// <summary>
    /// Activates the initial module (last-session <see cref="ShellSettings.ActiveModuleId"/>,
    /// else the first). Deliberately <em>not</em> done in the constructor: activation resolves
    /// a module's <c>ViewType</c> through the container, and if that graph transitively reaches
    /// <see cref="ShellViewModel"/> again the singleton would be constructed re-entrantly and
    /// deadlock the provider (#365). Call this once, from the shell bootstrap, <em>after</em>
    /// the window is shown and this singleton is fully built and cached. Idempotent.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        var initial = Modules.FirstOrDefault(e => e.Module.Id == _settings.ActiveModuleId)
                      ?? Modules.FirstOrDefault();
        if (initial is not null) ActivateModule(initial);
    }

    private void RefreshGameTime()
    {
        GameTimeText = _gameClock.GetCurrent().Format(_preferences.Use24HourClock);
        NextShiftCountdownText = BuildNextShiftCountdown();
    }

    /// <summary>
    /// "Next: &lt;Label&gt; in &lt;duration&gt;" beside the in-game-clock chip.
    /// Returns an empty string if the catalog is empty (the chip's row hides
    /// via NullOrEmptyToVis), so a degraded/missing shift table doesn't bleed
    /// into the shell's clock area. Formatting math lives in
    /// <see cref="JsonShiftCatalog.FormatRemaining"/> so the test project can
    /// pin the boundary cases without driving the shell view.
    /// </summary>
    private string BuildNextShiftCountdown()
    {
        if (_shiftCatalog.Shifts.Count == 0) return "";
        var floor = DateTimeOffset.UtcNow;
        var (at, shift) = _shiftCatalog.NextTransition(_gameClock, floor);
        return $"Next: {shift.Label} in {JsonShiftCatalog.FormatRemaining(at - floor)}";
    }

    private void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The 12/24h toggle should preview live — re-render the label as soon as
        // the user flips it, instead of waiting up to 5 s for the next tick.
        if (e.PropertyName == nameof(UserPreferences.Use24HourClock))
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) RefreshGameTime();
            else d.InvokeAsync(RefreshGameTime);
        }
    }

    private void OnPerfTraceStateChanged(object? sender, EventArgs e)
    {
        // The tracer fires from whichever thread called Start/Stop. ObservableProperty
        // setters touch INotifyPropertyChanged → bound XAML, so marshal to the UI dispatcher.
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) RefreshPerfTraceState();
        else d.InvokeAsync(RefreshPerfTraceState);
    }

    private void RefreshPerfTraceState()
    {
        IsPerfTraceRecording = _perf.IsActive;
        var path = _perf.CurrentSessionPath;
        PerfTraceTooltip = IsPerfTraceRecording
            ? (string.IsNullOrEmpty(path) ? "Perf-trace recording" : $"Recording → {path}")
            : "Perf-trace not recording";
    }

    private void OnHealthChanged(object? sender, EventArgs e)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) RefreshHealthState();
        else d.InvokeAsync(RefreshHealthState);
    }

    private void RefreshHealthState()
    {
        var player = _health.Player;
        var chat = _health.Chat;

        PipelineModeText = _health.AllLive ? "LIVE" : "REPLAY";
        PlayerDriftText = FormatDrift(player);
        ChatDriftText = FormatDrift(chat);
        IsHealthDegraded = _health.AllLive &&
            (player.Drift > TimeSpan.FromSeconds(5) || chat.Drift > TimeSpan.FromSeconds(5));
        HealthTooltip = $"Player: {player.Mode} · {player.FrameCount:N0} frames · drift {player.Drift.TotalSeconds:0.0}s\n" +
                        $"Chat: {chat.Mode} · {chat.FrameCount:N0} frames · drift {chat.Drift.TotalSeconds:0.0}s";
    }

    private static string FormatDrift(WorldHealth h)
    {
        if (h.LastTimestamp is null) return "—";
        var d = h.Drift;
        return d.TotalSeconds < 2 ? "<2s" : $"{d.TotalSeconds:0}s";
    }

    private void OnAttentionChanged(object? sender, AttentionChangedEventArgs e)
    {
        // Aggregator already marshalled to the UI thread.
        var entry = Modules.FirstOrDefault(m => m.Module.Id == e.ModuleId);
        if (entry is not null) entry.AttentionCount = e.Count;
        RefreshAttentionSurface();
    }

    private static readonly Lazy<System.Windows.Media.ImageSource> OverlayIcon =
        new(() => new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Mithril;component/Resources/attention-overlay.ico", UriKind.Absolute)));

    private void RefreshAttentionSurface()
    {
        var total = _attention.TotalCount;
        if (total > 0)
        {
            AttentionOverlayIcon = OverlayIcon.Value;
            AttentionTooltip = total == 1
                ? "Mithril — 1 item needs attention"
                : $"Mithril — {total} items need attention";
        }
        else
        {
            AttentionOverlayIcon = null;
            AttentionTooltip = "Mithril";
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellSettings.DeveloperMode)) return;
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) RebuildVisibleModules();
        else d.InvokeAsync(RebuildVisibleModules);
    }

    private void RebuildVisibleModules()
    {
        var keepActiveId = SelectedModule?.Module.Id;
        Modules.Clear();
        foreach (var m in _allModules)
        {
            if (m.IsDeveloperOnly && !_settings.DeveloperMode) continue;
            Modules.Add(new ModuleEntry
            {
                Module = m,
                AttentionCount = _attention.CountFor(m.Id),
            });
        }
        // If the previously selected module was just hidden, clear the selection
        // chip so the sidebar doesn't keep highlighting an absent entry.
        if (keepActiveId is not null && Modules.All(e => e.Module.Id != keepActiveId))
            SelectedModule = null;
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

    [ObservableProperty] private System.Windows.Media.ImageSource? _attentionOverlayIcon;
    [ObservableProperty] private string _attentionTooltip = "Mithril";

    [ObservableProperty] private string _gameTimeText = "";
    [ObservableProperty] private string _nextShiftCountdownText = "";

    [ObservableProperty] private bool _isPerfTraceRecording;
    [ObservableProperty] private string _perfTraceTooltip = "Perf-trace recording";

    [ObservableProperty] private string _pipelineModeText = "";
    [ObservableProperty] private string _playerDriftText = "";
    [ObservableProperty] private string _chatDriftText = "";
    [ObservableProperty] private bool _isHealthDegraded;
    [ObservableProperty] private string _healthTooltip = "";

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
            : $"Mithril v{v} is available — click Install to download and restart.";
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
    private void ActivateModuleById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var entry = Modules.FirstOrDefault(m => m.Module.Id == id);
        if (entry is not null) SelectedModule = entry;
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
        var sw = System.Diagnostics.Stopwatch.StartNew();

        SelectedModule = entry;
        _gates.For(entry.Module.Id).Open();          // Lazy modules wake up here
        var view = (System.Windows.Controls.Control)_services.GetRequiredService(entry.Module.ViewType);
        ActiveContent = view;
        _settings.ActiveModuleId = entry.Module.Id;
        StatusText = entry.Module.DisplayName;

        _perf.EmitModuleActivated(entry.Module.Id, sw.Elapsed.TotalMilliseconds);
    }
}
