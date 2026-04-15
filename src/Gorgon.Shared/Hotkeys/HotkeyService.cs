using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Gorgon.Shared.Hotkeys;

public sealed partial class HotkeyService : IHotkeyService
{
    private const int WM_HOTKEY = 0x0312;

    private readonly HotkeyRegistry _registry;
    private readonly Dictionary<int, IHotkeyCommand> _byRegistrationId = new();
    private readonly Dictionary<string, int> _byCommandId = new(StringComparer.Ordinal);
    private IReadOnlyList<HotkeyBinding> _lastBindings = Array.Empty<HotkeyBinding>();
    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _source;
    private int _nextId = 0xB000;
    private int _captureDepth;

    public HotkeyService(HotkeyRegistry registry) { _registry = registry; }

    public void Attach(IntPtr hwnd)
    {
        if (_source is not null) return;
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("HwndSource missing.");
        _source.AddHook(WndProc);
    }

    public IReadOnlyDictionary<string, string?> ReloadFromBindings(IEnumerable<HotkeyBinding> bindings)
    {
        _lastBindings = bindings.ToList();
        return RegisterAll();
    }

    private IReadOnlyDictionary<string, string?> RegisterAll()
    {
        var report = new Dictionary<string, string?>(StringComparer.Ordinal);
        UnregisterAll();
        if (_hwnd == IntPtr.Zero || _captureDepth > 0) return report;

        foreach (var binding in _lastBindings)
        {
            if (!_registry.TryGet(binding.CommandId, out var command))
            {
                report[binding.CommandId] = "Command no longer exists in this build.";
                continue;
            }
            var id = _nextId++;
            if (RegisterHotKey(_hwnd, id, (uint)binding.Modifiers, binding.VirtualKey))
            {
                _byRegistrationId[id] = command;
                _byCommandId[binding.CommandId] = id;
                report[binding.CommandId] = null;
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                report[binding.CommandId] = new Win32Exception(err).Message;
            }
        }
        return report;
    }

    private void UnregisterAll()
    {
        foreach (var id in _byRegistrationId.Keys.ToArray()) UnregisterHotKey(_hwnd, id);
        _byRegistrationId.Clear();
        _byCommandId.Clear();
    }

    public IDisposable BeginCaptureSession()
    {
        _captureDepth++;
        UnregisterAll();
        return new CaptureScope(this);
    }

    private void EndCaptureSession()
    {
        if (_captureDepth > 0) _captureDepth--;
        if (_captureDepth == 0) RegisterAll();
    }

    private sealed class CaptureScope : IDisposable
    {
        private readonly HotkeyService _svc;
        private bool _disposed;
        public CaptureScope(HotkeyService svc) { _svc = svc; }
        public void Dispose() { if (_disposed) return; _disposed = true; _svc.EndCaptureSession(); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        var id = wParam.ToInt32();
        if (!_byRegistrationId.TryGetValue(id, out var command)) return IntPtr.Zero;
        handled = true;
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { await command.ExecuteAsync(CancellationToken.None); } catch { }
        });
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
