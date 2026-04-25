namespace Mithril.Shared.Hotkeys;

public interface IHotkeyService : IDisposable
{
    void Attach(IntPtr hwnd);

    /// <summary>Re-registers all bindings against the OS. Returns commandId → error (null on success).</summary>
    IReadOnlyDictionary<string, string?> ReloadFromBindings(IEnumerable<HotkeyBinding> bindings);

    /// <summary>
    /// Temporarily unregisters all hotkeys for inline capture. Dispose to restore.
    /// </summary>
    IDisposable BeginCaptureSession();
}
