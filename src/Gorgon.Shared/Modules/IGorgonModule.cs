using Microsoft.Extensions.DependencyInjection;

namespace Gorgon.Shared.Modules;

public enum ActivationMode
{
    Lazy,
    Eager,
}

public interface IGorgonModule
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Emoji or short text glyph fallback when IconUri is unset or fails to load.</summary>
    string Icon { get; }
    /// <summary>Pack URI to a module-owned icon image (e.g. pack://application:,,,/Samwise.Module;component/Resources/samwise.ico).</summary>
    string? IconUri { get; }
    int SortOrder { get; }
    ActivationMode DefaultActivation { get; }
    Type ViewType { get; }
    void Register(IServiceCollection services);
}
