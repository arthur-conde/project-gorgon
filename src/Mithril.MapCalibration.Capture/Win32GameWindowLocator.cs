using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Game;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// CsWin32 implementation of <see cref="IGameWindowLocator"/>. Resolves the
/// foreground window, checks its process name against
/// <see cref="GameConfig.GameProcessName"/> (the same case-insensitive substring
/// rule <c>ForegroundFocusGate</c> uses, #919), and returns the client rect in
/// desktop pixels.
///
/// <para>The P/Invoke path is manual-verified against a running game (no
/// live-window CI test, consistent with the offline tools). Only the pure
/// <see cref="ProcessNameMatches"/> predicate is unit-tested.</para>
/// </summary>
public sealed class Win32GameWindowLocator : IGameWindowLocator
{
    private readonly GameConfig _gameConfig;
    private readonly ILogger? _logger;

    public Win32GameWindowLocator(GameConfig gameConfig, ILogger? logger = null)
    {
        _gameConfig = gameConfig;
        _logger = logger;
    }

    /// <summary>
    /// The configured-name → process-name match, lifted verbatim from
    /// <c>ForegroundFocusGate.IsForegroundInApp</c>: a non-blank configured name
    /// that the process image name contains (case-insensitive). An unconfigured
    /// (blank) name matches nothing.
    /// </summary>
    internal static bool ProcessNameMatches(string configured, string processName) =>
        !string.IsNullOrWhiteSpace(configured)
        && processName.Contains(configured, StringComparison.OrdinalIgnoreCase);

    public GameWindow? Locate()
    {
        try
        {
            HWND hwnd = PInvoke.GetForegroundWindow();
            if (hwnd.IsNull)
            {
                return null;
            }

            uint pid;
            unsafe { _ = PInvoke.GetWindowThreadProcessId(hwnd, &pid); }
            if (pid == 0)
            {
                return null;
            }

            string processName;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
            }
            catch
            {
                // Process exited between the lookup and us reading it, or access
                // denied (elevated). Treat as "not the game".
                return null;
            }

            if (!ProcessNameMatches(_gameConfig.GameProcessName, processName))
            {
                return null;
            }

            if (!PInvoke.GetClientRect(hwnd, out RECT client))
            {
                _logger?.LogWarning("GetClientRect failed for the foreground game window");
                return null;
            }

            // Client rect is in client coords (top-left = 0,0); translate to
            // desktop pixels via ClientToScreen on the top-left corner.
            var topLeft = new System.Drawing.Point(client.left, client.top);
            if (!PInvoke.ClientToScreen(hwnd, ref topLeft))
            {
                _logger?.LogWarning("ClientToScreen failed for the foreground game window");
                return null;
            }

            int width = client.right - client.left;
            int height = client.bottom - client.top;
            var rect = new CaptureRect(topLeft.X, topLeft.Y, width, height);
            if (rect.IsEmpty)
            {
                _logger?.LogWarning("Foreground game window has an empty client rect ({Width}x{Height})", width, height);
                return null;
            }

            nint hwndValue;
            unsafe { hwndValue = (nint)hwnd.Value; }
            return new GameWindow(hwndValue, rect);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to locate the foreground game window");
            return null;
        }
    }
}
