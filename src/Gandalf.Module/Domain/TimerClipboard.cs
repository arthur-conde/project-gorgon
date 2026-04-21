using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gandalf.Domain;

public sealed class TimerClipboardEntry
{
    public string Name { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Region { get; set; } = "";
    public string Map { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TimerClipboardEntry))]
[JsonSerializable(typeof(List<TimerClipboardEntry>))]
public partial class TimerClipboardJsonContext : JsonSerializerContext { }

public static class TimerClipboard
{
    public static string Serialize(IEnumerable<GandalfTimer> timers)
    {
        var entries = timers.Select(t => new TimerClipboardEntry
        {
            Name = t.Name,
            Duration = t.Duration.ToString(),
            Region = t.Region,
            Map = t.Map,
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

    public static GandalfTimer? ToTimer(TimerClipboardEntry entry)
    {
        if (!TimeSpan.TryParse(entry.Duration, out var dur) || dur <= TimeSpan.Zero) return null;
        return new GandalfTimer
        {
            Name = entry.Name,
            Duration = dur,
            Region = entry.Region,
            Map = entry.Map,
        };
    }
}
