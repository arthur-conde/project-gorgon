using System.IO;
using Arda.Composition;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Smaug.Domain;
using Smaug.State;
using Smaug.ViewModels;
using Smaug.Views;

namespace Smaug;

public sealed class SmaugModule : IMithrilModule
{
    public string Id => "smaug";
    public string DisplayName => "Smaug · Vendor Prices";
    public PackIconLucideKind Icon => PackIconLucideKind.Coins;
    public string? IconUri => "pack://application:,,,/Smaug.Module;component/Resources/smaug.ico";
    public int SortOrder => 260;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(SmaugView);
    public Type? SettingsViewType => typeof(SmaugSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Path.Combine(localApp, "Mithril", "Smaug", "settings.json");

        services.AddMithrilSettings<SmaugSettings>(settingsPath, SmaugJsonContext.Default.SmaugSettings);

        services.AddSingleton<VendorCatalogService>();
        services.AddSingleton<StorageSellbackService>();
        services.AddSingleton<SellPlannerService>();
        services.AddSingleton<PriceCalibrationService>(sp => new PriceCalibrationService(
            sp.GetRequiredService<IReferenceDataService>(),
            Path.Combine(localApp, "Mithril", "Smaug"),
            sp.GetService<ICommunityCalibrationService>(),
            sp.GetRequiredService<SmaugSettings>().Calibration,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Smaug"),
            sp.GetService<ISessionComposer>()));

        services.AddSingleton<VendorCatalogViewModel>();
        services.AddSingleton<VendorShopViewModel>();
        services.AddSingleton<StorageSellbackViewModel>();
        services.AddSingleton<SellPlannerViewModel>();
        services.AddSingleton<SellPricesViewModel>();
        services.AddSingleton<CalibrationViewModel>();

        services.AddSingleton<SmaugShellViewModel>();
        services.AddSingleton<SmaugView>(sp => new SmaugView
        {
            DataContext = sp.GetRequiredService<SmaugShellViewModel>(),
        });
        services.AddSingleton<SmaugSettingsView>(sp => new SmaugSettingsView
        {
            DataContext = sp.GetRequiredService<SmaugSettings>(),
        });

        services.AddHostedService<VendorIngestionService>();
    }
}
