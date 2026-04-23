namespace Gorgon.Shared.Character;

/// <summary>
/// Marker for a persisted root that carries a schema version. Use
/// <see cref="IVersionedState{T}"/> when you also need migration.
/// </summary>
public interface IVersionedState
{
    int SchemaVersion { get; set; }
}

/// <summary>
/// Per-character / per-character-per-module persisted roots implement this so that
/// <see cref="PerCharacterStore{T}"/> can dispatch through a migration hook at load time.
/// The state type owns its current version and its upgrade path — the store is shape-agnostic.
/// </summary>
public interface IVersionedState<T> : IVersionedState
    where T : class, IVersionedState<T>, new()
{
    /// <summary>Latest schema version this state type supports.</summary>
    static abstract int CurrentVersion { get; }

    /// <summary>
    /// Convert a loaded instance (whose <see cref="IVersionedState.SchemaVersion"/> may be older)
    /// into a <see cref="CurrentVersion"/>-shaped instance. Identity passthrough is fine until a
    /// breaking change lands. Called on every load; must tolerate <see cref="CurrentVersion"/>
    /// input as a no-op.
    /// </summary>
    static abstract T Migrate(T loaded);
}
