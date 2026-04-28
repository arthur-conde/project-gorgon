using Palantir.Domain;
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
    public Type ViewType => typeof(PalantirView);
    public Type? SettingsViewType => null;
    public bool IsDeveloperOnly => true;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<LiveInventoryViewModel>();
        services.AddSingleton<LiveInventoryView>(sp => new LiveInventoryView
        {
            DataContext = sp.GetRequiredService<LiveInventoryViewModel>(),
        });

        services.AddSingleton<PalantirAttentionSource>();
        services.AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<PalantirAttentionSource>());

        services.AddSingleton<NotificationTesterViewModel>();
        services.AddSingleton<NotificationTesterView>(sp => new NotificationTesterView
        {
            DataContext = sp.GetRequiredService<NotificationTesterViewModel>(),
        });

        services.AddSingleton<PalantirView>(sp =>
        {
            var view = new PalantirView();
            view.AddTab("Live Inventory", sp.GetRequiredService<LiveInventoryView>());
            view.AddTab("Notification Tester", sp.GetRequiredService<NotificationTesterView>());
            return view;
        });
    }
}
