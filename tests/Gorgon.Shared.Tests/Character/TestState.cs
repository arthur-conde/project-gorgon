using System.Text.Json.Serialization;
using Gorgon.Shared.Character;

namespace Gorgon.Shared.Tests.Character;

/// <summary>A minimal <see cref="IVersionedState{T}"/> used by the character-storage tests.</summary>
internal sealed class TestState : IVersionedState<TestState>
{
    public const int Version = 2;
    public static int CurrentVersion => Version;

    /// <summary>If non-null, <see cref="Migrate"/> asserts the loaded version and bumps to current.</summary>
    public static Func<TestState, TestState>? MigrateOverride { get; set; }

    public static TestState Migrate(TestState loaded)
        => MigrateOverride is null ? loaded : MigrateOverride(loaded);

    public int SchemaVersion { get; set; } = Version;
    public string Value { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestState))]
internal partial class TestStateJsonContext : JsonSerializerContext { }
