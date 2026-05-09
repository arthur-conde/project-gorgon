namespace Mithril.Shared.Settings;

/// <summary>
/// Hook called by <see cref="JsonSettingsStore{T}.Load"/> /
/// <see cref="JsonSettingsStore{T}.LoadAsync"/> immediately after
/// deserialization completes. Settings types use this to wire
/// <see cref="SettingsNode.Bubble"/> subscriptions on freshly-loaded child
/// nodes — STJ source-gen populates property values without invoking
/// constructors that would have wired them, and <c>[OnDeserialized]</c>
/// callbacks aren't supported by the source-generated path either.
///
/// Implementations must be idempotent: <c>Load</c> may be called multiple
/// times in tests, and a stray re-call must not double-subscribe.
/// </summary>
public interface IPostLoadInit
{
    void PostLoadInit();
}
