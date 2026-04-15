namespace Gorgon.Shared.Modules;

/// <summary>
/// Per-module activation latch. Hosted services that should only run after
/// activation await <see cref="WaitAsync"/>; the shell calls <see cref="Open"/>
/// at startup for Eager modules and on first tab selection for Lazy ones.
/// </summary>
public sealed class ModuleGate
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WaitAsync(CancellationToken ct = default)
    {
        if (_tcs.Task.IsCompleted) return Task.CompletedTask;
        if (!ct.CanBeCanceled) return _tcs.Task;
        var tcs = new TaskCompletionSource();
        var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        _tcs.Task.ContinueWith(_ => { reg.Dispose(); tcs.TrySetResult(); }, TaskScheduler.Default);
        return tcs.Task;
    }
    public void Open() => _tcs.TrySetResult();
    public bool IsOpen => _tcs.Task.IsCompleted;
}

public sealed class ModuleGates
{
    private readonly Dictionary<string, ModuleGate> _gates = new(StringComparer.Ordinal);
    public ModuleGate For(string moduleId)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(moduleId, out var g)) _gates[moduleId] = g = new ModuleGate();
            return g;
        }
    }
}
