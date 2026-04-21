using System.Text.Json.Serialization;

namespace Gandalf.Domain;

public sealed class GandalfState
{
    public List<GandalfTimer> Timers { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfState))]
public partial class GandalfStateJsonContext : JsonSerializerContext { }
