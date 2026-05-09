using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gandalf.Domain;

public sealed class TimerClipboardEntry
{
    public string Name { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Area { get; set; } = "";
    public string? AreaKey { get; set; }
    public string? SoundFilePath { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TimerClipboardEntry))]
[JsonSerializable(typeof(List<TimerClipboardEntry>))]
public partial class TimerClipboardJsonContext : JsonSerializerContext { }

public static class TimerClipboard
{
    public static string Serialize(IEnumerable<GandalfTimerDef> defs)
    {
        var entries = defs.Select(d => new TimerClipboardEntry
        {
            Name = d.Name,
            Duration = d.Duration.ToString(),
            Area = d.Area,
            AreaKey = d.AreaKey,
            SoundFilePath = d.SoundFilePath,
        }).ToList();
        return JsonSerializer.Serialize(entries, TimerClipboardJsonContext.Default.ListTimerClipboardEntry);
    }

    public static List<TimerClipboardEntry>? TryDeserialize(string json)
    {
        try
        {
            json = json.Trim();
            if (json.StartsWith('['))
                return JsonSerializer.Deserialize(json, TimerClipboardJsonContext.Default.ListTimerClipboardEntry);
            if (json.StartsWith('{'))
            {
                var single = JsonSerializer.Deserialize(json, TimerClipboardJsonContext.Default.TimerClipboardEntry);
                return single is not null ? [single] : null;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds a fresh definition (new Id, no progress) from a clipboard entry. Returns null
    /// when the duration is missing or non-positive.
    /// </summary>
    public static GandalfTimerDef? ToDef(TimerClipboardEntry entry)
    {
        if (!TimeSpan.TryParse(entry.Duration, out var dur) || dur <= TimeSpan.Zero) return null;
        return new GandalfTimerDef
        {
            Name = entry.Name,
            Duration = dur,
            Area = entry.Area,
            AreaKey = string.IsNullOrWhiteSpace(entry.AreaKey) ? null : entry.AreaKey,
            SoundFilePath = string.IsNullOrWhiteSpace(entry.SoundFilePath) ? null : entry.SoundFilePath,
        };
    }
}
