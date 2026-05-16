using Microsoft.Extensions.DependencyInjection;
using Mithril.Shared.Modules;
using Mithril.Shell.ViewModels;

namespace Mithril.Shell;

/// <summary>
/// Bridge between shared services (deep-link handlers) and the shell's tab switcher. Looks up
/// the <see cref="ModuleEntry"/> for a module id and assigns it as <c>SelectedModule</c>, which
/// triggers the normal activate flow (gate open, view resolution, settings persistence).
/// </summary>
/// <remarks>
/// <see cref="ShellViewModel"/> is resolved <em>lazily</em>, not via constructor injection.
/// Deep-link handlers (and anything else that pulls <see cref="IModuleActivator"/>) are
/// constructed eagerly while the root provider is still building
/// (<c>GetRequiredService&lt;IDeepLinkRouter&gt;</c> at startup); a hard ctor dependency on
/// <see cref="ShellViewModel"/> turned any module→activator→shell edge into a re-entrant
/// singleton-construction deadlock (#365, regression from #359). Resolving on first
/// <see cref="Activate"/> instead means the shell is fully built and cached by the time this
/// is ever called.
/// </remarks>
public sealed class ShellModuleActivator : IModuleActivator
{
    private readonly IServiceProvider _services;

    public ShellModuleActivator(IServiceProvider services) => _services = services;

    public bool Activate(string moduleId)
    {
        var shell = _services.GetRequiredService<ShellViewModel>();
        var entry = shell.Modules.FirstOrDefault(e =>
            string.Equals(e.Module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        shell.SelectedModule = entry;
        return true;
    }
}
