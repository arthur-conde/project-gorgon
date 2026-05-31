using System.IO;
using System.Text.Json;
using Mithril.MapCalibration;

namespace Mithril.Shell;

/// <summary>
/// One-time composition-root carry-over (#957) of the retired
/// <c>LegolasSettings.MapOverlay</c> (the overlay window's persisted position, DIUs,
/// in <c>legolas/settings.json</c>) into <see cref="ShellSettings.MapCaptureBbox"/>
/// (the one-rect capture frame, physical px, in <c>shell.json</c>).
///
/// <para><b>Why a cross-file carry-over (mirrors <see cref="GameConfigCarryOver"/>,
/// #919).</b> The value moves <em>between files</em> and <em>between units</em>
/// (DIU → physical), so a per-file <c>IVersionedState.Migrate</c> can reach neither
/// across files nor at the right unit. We read the pre-retirement <c>mapOverlay</c>
/// rect straight out of the raw <c>legolas/settings.json</c> on disk (the key
/// persists there for upgrading users even though <c>LegolasSettings</c> no longer
/// declares the field) and seed the shell store <strong>only when the shell rect is
/// still unset</strong> (<see cref="ShellSettings.MapCaptureBbox"/> is
/// <see langword="null"/>). That makes it idempotent: once the user has snipped or
/// dragged (shell rect non-null) we never clobber it, and on a fresh install with no
/// legolas file it is a no-op.</para>
///
/// <para><b>Why this matters.</b> #947 shipped <see cref="ShellSettings.MapCaptureBbox"/>
/// null-default, so an upgrading user's last overlay-derived capture region was
/// already reset to "no bbox". Recovering it from the still-persisted overlay
/// position restores the one-rect for upgraders instead of forcing a re-snip.</para>
///
/// <para><b>DPI.</b> DIU → physical needs a device scale. At bootstrap no overlay
/// window exists, so the caller supplies the system/primary DPI scale — exact for
/// the supported single-/uniform-DPI case; a true mixed-DPI multi-monitor layout is
/// #938 best-effort (the seeded rect may be off-scale; the user re-snips to correct).</para>
/// </summary>
public static class MapCaptureRectCarryOver
{
    // The retired LegolasSettings.MapOverlay factory default (its initializer was
    // `new() { Width = 800, Height = 600 }`, over WindowLayout's Left=100/Top=100).
    // A persisted rect still equal to this means the user never positioned the
    // overlay, so there is no meaningful frame to migrate — leave the shell rect
    // unset and let the user snip fresh.
    internal const double DefaultLeft = 100;
    internal const double DefaultTop = 100;
    internal const double DefaultWidth = 800;
    internal const double DefaultHeight = 600;

    /// <summary>
    /// Mutates <paramref name="shell"/> in place. Safe to call on every startup; only
    /// the first run (shell rect still null + a non-default legolas overlay rect)
    /// changes anything.
    /// </summary>
    /// <param name="legolasSettingsPath">Path to the module's <c>settings.json</c>.</param>
    /// <param name="shell">The loaded shared shell settings.</param>
    /// <param name="dpiScale">System/primary device scale (1.0 at 100%, 1.5 at 150%)
    /// used to convert the legacy DIU rect to physical pixels.</param>
    /// <returns><c>true</c> if the bbox was carried over (so the caller can persist).</returns>
    public static bool Apply(string legolasSettingsPath, ShellSettings shell, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (shell.MapCaptureBbox is not null) return false; // already set — never clobber
        if (string.IsNullOrEmpty(legolasSettingsPath) || !File.Exists(legolasSettingsPath))
            return false;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legolasSettingsPath));
            root = doc.RootElement.Clone(); // survive the using-block dispose
        }
        catch
        {
            // Corrupt / unreadable legolas settings — nothing to carry. The module's
            // own loader will surface any real problem.
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("mapOverlay", out var overlay)
            || overlay.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetNumber(overlay, "left", out var left)
            || !TryGetNumber(overlay, "top", out var top)
            || !TryGetNumber(overlay, "width", out var width)
            || !TryGetNumber(overlay, "height", out var height))
            return false;

        if (width <= 0 || height <= 0) return false; // degenerate — nothing to capture

        // Untouched factory default → no meaningful frame to migrate.
        if (left == DefaultLeft && top == DefaultTop
            && width == DefaultWidth && height == DefaultHeight)
            return false;

        var physical = CaptureRectMath.DiuToPhysical(left, top, width, height, dpiScale, dpiScale);
        if (physical.IsEmpty) return false;

        shell.MapCaptureBbox = new MapCaptureBbox
        {
            Left = physical.X,
            Top = physical.Y,
            Width = physical.Width,
            Height = physical.Height,
        };
        return true;
    }

    private static bool TryGetNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetDouble(out value);
    }
}
