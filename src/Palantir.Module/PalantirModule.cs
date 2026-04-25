using Palantir.ViewModels;
using Palantir.Views;
using Mithril.Shared.Modules;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Palantir;

public sealed class PalantirModule : IMithrilModule
{
    public string Id => "palantir";
    public string DisplayName => "Palantir · Inventory";
    public PackIconLucideKind Icon => PackIconLucideKind.Eye;
    public string? IconUri => "pack://application:,,,/Palantir.Module;component/Resources/palantir.ico";
    public int SortOrder => 900;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(LiveInventoryView);
    public Type? SettingsViewType => null;
    public bool IsDeveloperOnly => true;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<LiveInventoryViewModel>();
        services.AddSingleton<LiveInventoryView>(sp => new LiveInventoryView
        {
            DataContext = sp.GetRequiredService<LiveInventoryViewModel>(),
        });
    }
}
