using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Writes a captured frame to disk as a PNG for debugging (#966 Task 3), using
/// WPF/WIC encoders (<see cref="BitmapSource.Create"/> + <see cref="PngBitmapEncoder"/>) —
/// NOT <c>System.Drawing</c>, so the decoder-free guard (#921) stays satisfied
/// (the Capture csproj already sets <c>UseWPF=true</c>).
///
/// <para>Fail-soft by contract: every public method swallows and logs its own
/// errors, so a dump failure can never fail the capture it instruments.</para>
/// </summary>
public sealed class CaptureFrameDumper
{
    private readonly ILogger? _logger;

    public CaptureFrameDumper(ILogger? logger) => _logger = logger;

    /// <summary>
    /// Directory PNGs are written to: <c>%LocalAppData%/Mithril/diagnostics/calibration</c>.
    /// </summary>
    public static string DumpDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "diagnostics", "calibration");

    /// <summary>
    /// Persist <paramref name="frame"/>'s BGRA pixels as a PNG. The filename is
    /// <c>&lt;tag&gt;-&lt;yyyyMMdd-HHmmss-fff&gt;.png</c> where <paramref name="tag"/> is a
    /// caller-supplied stable label (area key when available, else a bbox-derived
    /// fallback). Returns the written path, or null on any failure (logged).
    /// </summary>
    public string? DumpColor(CapturedFrame frame, string tag)
    {
        try
        {
            int stride = frame.Width * 4; // BGRA32, 4 bytes/pixel, top-down rows
            var source = BitmapSource.Create(
                frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, stride);
            return Encode(source, tag, "color");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Capture-frame color dump failed for {Tag}; capture continues.", tag);
            return null;
        }
    }

    /// <summary>
    /// Persist the derived grayscale image as an 8-bit PNG. Catches a
    /// <see cref="CapturedFrame.ToGray"/> bug a color-only dump would hide. Returns
    /// the written path, or null on any failure (logged).
    /// </summary>
    public string? DumpGray(GrayImage gray, string tag)
    {
        try
        {
            int stride = gray.Width; // Gray8, 1 byte/pixel
            var source = BitmapSource.Create(
                gray.Width, gray.Height, 96, 96, PixelFormats.Gray8, null, gray.Pixels, stride);
            return Encode(source, tag, "gray");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Capture-frame gray dump failed for {Tag}; capture continues.", tag);
            return null;
        }
    }

    private string? Encode(BitmapSource source, string tag, string kind)
    {
        Directory.CreateDirectory(DumpDirectory);
        source.Freeze();
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string path = Path.Combine(DumpDirectory, $"{Sanitize(tag)}-{kind}-{stamp}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var fs = File.Create(path))
        {
            encoder.Save(fs);
        }
        _logger?.LogInformation("Capture-frame {Kind} dump written: {Path}", kind, path);
        return path;
    }

    /// <summary>Strips path-hostile characters from a caller tag so it is filename-safe.</summary>
    private static string Sanitize(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "capture";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            tag = tag.Replace(c, '_');
        }
        return tag;
    }
}
