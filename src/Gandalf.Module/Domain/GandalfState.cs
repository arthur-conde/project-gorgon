using System.Text.Json.Serialization;
using Gorgon.Shared.Character;

namespace Gandalf.Domain;

public sealed class GandalfState : IVersionedState<GandalfState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static GandalfState Migrate(GandalfState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    public List<GandalfTimer> Timers { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfState))]
public partial class GandalfStateJsonContext : JsonSerializerContext { }
