using Mithril.Shared.Modules;
using Mithril.Shell.ViewModels;

namespace Mithril.Shell;

/// <summary>
/// Bridge between shared services (deep-link handlers) and the shell's tab switcher. Looks up
/// the <see cref="ModuleEntry"/> for a module id and assigns it as <c>SelectedModule</c>, which
/// triggers the normal activate flow (gate open, view resolution, settings persistence).
/// </summary>
public sealed class ShellModuleActivator : IModuleActivator
{
    private readonly ShellViewModel _shell;

    public ShellModuleActivator(ShellViewModel shell) => _shell = shell;

    public bool Activate(string moduleId)
    {
        var entry = _shell.Modules.FirstOrDefault(e =>
            string.Equals(e.Module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        _shell.SelectedModule = entry;
        return true;
    }
}
