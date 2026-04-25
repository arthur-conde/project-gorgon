using Microsoft.Win32;

namespace Mithril.Shell;

/// <summary>
/// Manages the opt-in <c>mithril://</c> URL-scheme registration under
/// <c>HKCU\Software\Classes\mithril</c>. Per-user (no elevation), idempotent,
/// removable. Drives the toggle button in About settings.
/// </summary>
public static class MithrilUriSchemeRegistrar
{
    public const string Scheme = "mithril";
    private const string RootPath = @"Software\Classes\" + Scheme;
    private const string CommandPath = RootPath + @"\shell\open\command";

    /// <summary>True when the scheme key exists under HKCU.</summary>
    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RootPath);
        return key is not null;
    }

    /// <summary>
    /// Returns the exe path currently configured as the handler, or null when not
    /// registered. Used by the About view to distinguish "registered to this install"
    /// from "registered to a different install on this machine".
    /// </summary>
    public static string? CurrentRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(CommandPath);
        return key?.GetValue(null) as string;
    }

    /// <summary>Writes the three HKCU keys that tell Windows to launch <paramref name="exePath"/> for mithril:// links.</summary>
    public static void Register(string exePath)
    {
        using (var schemeKey = Registry.CurrentUser.CreateSubKey(RootPath))
        {
            schemeKey.SetValue("", "URL:Mithril Protocol");
            schemeKey.SetValue("URL Protocol", "");
        }
        using var commandKey = Registry.CurrentUser.CreateSubKey(CommandPath);
        commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
    }

    /// <summary>Removes the scheme registration. No-op when already absent.</summary>
    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(RootPath, throwOnMissingSubKey: false);
    }
}
