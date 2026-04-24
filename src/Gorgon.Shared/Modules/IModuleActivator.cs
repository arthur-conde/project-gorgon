namespace Gorgon.Shared.Modules;

/// <summary>
/// Cross-cutting hook that lets shared services (deep-link handlers, notifications, …)
/// bring a specific module's tab to the foreground without taking a hard reference on the
/// shell view model. Implemented by <c>Gorgon.Shell</c>.
/// </summary>
public interface IModuleActivator
{
    /// <summary>Selects the module tab with the given id. Returns true if the module was found.</summary>
    bool Activate(string moduleId);
}
