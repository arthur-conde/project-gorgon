namespace Mithril.Shared.Character;

/// <summary>
/// Shell-configured root for per-character storage. Registered as a singleton via
/// <c>AddMithrilPerCharacterStorage</c>; consumed by every
/// <see cref="PerCharacterStore{T}"/> factory.
/// </summary>
public sealed class PerCharacterStoreOptions
{
    public required string CharactersRootDir { get; init; }
}
