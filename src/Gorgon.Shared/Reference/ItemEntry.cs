namespace Gorgon.Shared.Reference;

/// <summary>
/// Slim projection of one entry in items.json. Matches the fields the HTML
/// helper extracts, which covers icon rendering, stack-aware tooling, and
/// seed-to-crop name resolution.
/// </summary>
public sealed record ItemEntry(long Id, string Name, string InternalName, int MaxStackSize, int IconId);
