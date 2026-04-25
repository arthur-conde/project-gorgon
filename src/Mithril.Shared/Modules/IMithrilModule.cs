using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Mithril.Shared.Modules;

public enum ActivationMode
{
    Lazy,
    Eager,
}

public interface IMithrilModule
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Lucide icon kind shown in the sidebar and settings.</summary>
    PackIconLucideKind Icon { get; }
    /// <summary>Pack URI to a module-owned icon image (e.g. pack://application:,,,/Samwise.Module;component/Resources/samwise.ico).</summary>
    string? IconUri { get; }
    int SortOrder { get; }
    ActivationMode DefaultActivation { get; }
    Type ViewType { get; }
    /// <summary>
    /// Optional settings view resolved from DI and rendered inside the shell's
    /// Settings host. Null if the module has nothing user-configurable.
    /// </summary>
    Type? SettingsViewType { get; }
    void Register(IServiceCollection services);
}
