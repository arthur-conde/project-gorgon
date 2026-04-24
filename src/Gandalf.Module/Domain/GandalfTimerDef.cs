using System.Text.Json.Serialization;

namespace Gandalf.Domain;

/// <summary>
/// Immutable shape of a timer definition — what the user configured, shared across every character.
/// Lives in the global <see cref="GandalfDefinitions"/> blob. Progress for this timer is tracked
/// per-character in <see cref="GandalfProgress"/> keyed by <see cref="Id"/>.
/// </summary>
public sealed class GandalfTimerDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string Region { get; set; } = "";
    public string Map { get; set; } = "";

    [JsonIgnore]
    public string GroupKey => string.IsNullOrWhiteSpace(Map)
        ? Region
        : $"{Region} > {Map}";
}
