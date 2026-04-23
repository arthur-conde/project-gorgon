using System.IO;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Settings;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Smaug.Domain;
using Smaug.Parsing;
using Smaug.State;
using Smaug.ViewModels;
using Smaug.Views;

namespace Smaug;

public sealed class SmaugModule : IGorgonModule
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
        var settingsPath = Path.Combine(localApp, "Gorgon", "Smaug", "settings.json");

        services.AddSingleton<ISettingsStore<SmaugSettings>>(_ =>
            new JsonSettingsStore<SmaugSettings>(settingsPath, SmaugJsonContext.Default.SmaugSettings));
        services.AddSingleton<SmaugSettings>(sp =>
            sp.GetRequiredService<ISettingsStore<SmaugSettings>>().Load());
        services.AddSingleton<SettingsAutoSaver<SmaugSettings>>();

        services.AddSingleton<VendorLogParser>();
        services.AddSingleton<VendorSellContext>();
        services.AddSingleton<VendorCatalogService>();
        services.AddSingleton<StorageSellbackService>();
        services.AddSingleton<PriceCalibrationService>(sp => new PriceCalibrationService(
            sp.GetRequiredService<IReferenceDataService>(),
            Path.Combine(localApp, "Gorgon", "Smaug"),
            sp.GetService<ICommunityCalibrationService>(),
            sp.GetRequiredService<SmaugSettings>().Calibration,
            sp.GetService<IDiagnosticsSink>()));

        services.AddSingleton<VendorCatalogViewModel>();
        services.AddSingleton<VendorShopViewModel>();
        services.AddSingleton<StorageSellbackViewModel>();
        services.AddSingleton<SellPricesViewModel>();
        services.AddSingleton<CalibrationViewModel>();

        services.AddSingleton<SmaugView>(sp =>
        {
            var view = new SmaugView();
            view.AddTab("Vendor Shop", new VendorShopTab { DataContext = sp.GetRequiredService<VendorShopViewModel>() });
            view.AddTab("Storage Sellback", new StorageSellbackTab { DataContext = sp.GetRequiredService<StorageSellbackViewModel>() });
            view.AddTab("Vendor Catalog", new VendorCatalogTab { DataContext = sp.GetRequiredService<VendorCatalogViewModel>() });
            view.AddTab("Sell Prices", new SellPricesTab { DataContext = sp.GetRequiredService<SellPricesViewModel>() });
            view.AddTab("Calibration", new CalibrationTab { DataContext = sp.GetRequiredService<CalibrationViewModel>() });
            return view;
        });
        services.AddSingleton<SmaugSettingsView>(sp => new SmaugSettingsView
        {
            DataContext = sp.GetRequiredService<SmaugSettings>(),
        });

        services.AddHostedService<VendorIngestionService>();
    }
}
