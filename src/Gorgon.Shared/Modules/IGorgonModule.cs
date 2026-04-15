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
    string Icon { get; }
    int SortOrder { get; }
    ActivationMode DefaultActivation { get; }
    Type ViewType { get; }
    void Register(IServiceCollection services);
}
