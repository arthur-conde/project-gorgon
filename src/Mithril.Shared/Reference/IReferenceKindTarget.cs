namespace Mithril.Shared.Reference;

/// <summary>
/// A single entity-kind dispatch target registered with the Silmarillion
/// reference navigator. Each tab module registers one implementation; the
/// navigator and the host VM enumerate registered targets instead of
/// enumerating <c>switch (EntityKind)</c> cases (issue #239).
///
/// Replaces the hardcoded <c>V1TabbedKinds</c> HashSet and the two switches
/// in <c>SilmarillionViewModel</c>. As Bucket-B tabs ship (NPCs → Quests →
/// Areas+Landmarks → Lorebooks → Abilities → Effects → PlayerTitles →
/// StorageVaults) each adds one more target — no touches to the navigator
/// or the host VM.
/// </summary>
public interface IReferenceKindTarget
{
    /// <summary>The entity kind this target is responsible for. One target per kind.</summary>
    EntityKind Kind { get; }

    /// <summary>The TabControl index this target's UI lives at.</summary>
    int TabIndex { get; }

    /// <summary>
    /// Look the entity up by internal name and select it in the tab's
    /// master-detail. Returns false if the entity isn't in the reference data
    /// (e.g. a stale deep link).
    /// </summary>
    bool TrySelectByInternalName(string internalName);

    /// <summary>
    /// Open the current detail in a popup window. Returns false if the tab
    /// has no current detail (nothing selected).
    /// </summary>
    bool TryOpenInWindow();
}
