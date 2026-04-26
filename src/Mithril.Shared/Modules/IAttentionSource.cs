namespace Mithril.Shared.Modules;

/// <summary>
/// One module's contribution to the shell's "attention required" surface.
/// A module opts in by registering an implementation as a singleton in
/// <c>IServiceCollection</c> during <see cref="IMithrilModule.Register"/>.
/// Modules with no attention state simply don't register one.
/// </summary>
public interface IAttentionSource
{
    /// <summary>Stable id matching <see cref="IMithrilModule.Id"/> (e.g. "arwen").</summary>
    string ModuleId { get; }

    /// <summary>Human label for context menus / tooltips (e.g. "Arwen — gifts pending").</summary>
    string DisplayLabel { get; }

    /// <summary>Live count. 0 means "nothing needs attention."</summary>
    int Count { get; }

    /// <summary>Raised whenever <see cref="Count"/> may have changed. Payload-free; consumers re-read <see cref="Count"/>.</summary>
    event EventHandler? Changed;
}
