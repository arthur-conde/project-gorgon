using Mithril.Shared.Reference;

namespace Mithril.TestSupport;

/// <summary>
/// Minimal <see cref="IEntityNameResolver"/> stand-in for tests that want to assert
/// the resolved friendly name without spinning up <see cref="ReferenceDataEntityNameResolver"/>
/// over a real refData fixture. Unmapped entries fall through to
/// <see cref="EntityRef.InternalName"/> so the empty constructor is fine for tests
/// that don't depend on the resolved name.
/// </summary>
internal sealed class FakeEntityNameResolver : IEntityNameResolver
{
    private readonly Dictionary<EntityRef, string> _map;

    public FakeEntityNameResolver(params (EntityKind kind, string internalName, string friendly)[] entries) =>
        _map = entries.ToDictionary(
            e => new EntityRef(e.kind, e.internalName),
            e => e.friendly);

    public string Resolve(EntityRef reference) =>
        _map.TryGetValue(reference, out var friendly) ? friendly : reference.InternalName;
}
