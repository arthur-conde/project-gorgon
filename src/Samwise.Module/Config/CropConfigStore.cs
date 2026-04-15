using System.IO;
using System.Text.Json;

namespace Samwise.Config;

public interface ICropConfigStore
{
    CropConfig Current { get; }
    event EventHandler? Reloaded;
    Task ReloadAsync(CancellationToken ct = default);
}

public sealed class CropConfigStore : ICropConfigStore, IDisposable
{
    private readonly string _bundledPath;
    private readonly string _userPath;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;

    public CropConfigStore(string bundledPath, string userPath)
    {
        _bundledPath = bundledPath;
        _userPath = userPath;
        Current = LoadSync();
        WatchUser();
    }

    public CropConfig Current { get; private set; }

    public event EventHandler? Reloaded;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var fresh = await Task.Run(LoadSync, ct).ConfigureAwait(false);
        Current = fresh;
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    private CropConfig LoadSync()
    {
        var bundled = ReadFile(_bundledPath) ?? new CropConfig();
        var user = ReadFile(_userPath);
        if (user is null) { bundled.InvalidateCaches(); return bundled; }
        // Deep-merge: user crops override bundled by name; user slot families override
        foreach (var (k, v) in user.SlotFamilies) bundled.SlotFamilies[k] = v;
        foreach (var (k, v) in user.Crops) bundled.Crops[k] = v;
        bundled.InvalidateCaches();
        return bundled;
    }

    private static CropConfig? ReadFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize(fs, CropConfigJsonContext.Default.CropConfig);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private void WatchUser()
    {
        var dir = Path.GetDirectoryName(_userPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_userPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += (_, __) => OnChanged(this, EventArgs.Empty);
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        _ = DebounceReload(ct);
    }

    private async Task DebounceReload(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            await ReloadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
