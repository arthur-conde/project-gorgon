namespace Mithril.Shared.Reference;

/// <summary>
/// Resolves the friendly display name for an entity reference. Centralises the
/// per-kind fallback chain (POCO <c>Name</c> field → kind-specific prefix-stripping →
/// raw internal name) so each Silmarillion tab can surface NPC / item / recipe names
/// without re-implementing the fallback in chip-builders, master-list rows, and detail
/// headers. New entity kinds extend the switch in
/// <see cref="ReferenceDataEntityNameResolver"/>; unknown kinds return the raw
/// internal name so a freshly-added <see cref="EntityKind"/> still renders something
/// readable before its case lands.
/// </summary>
public interface IEntityNameResolver
{
    string Resolve(EntityRef reference);
}
