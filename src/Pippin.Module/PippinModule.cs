using System.IO;
using Gorgon.Shared.Character;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Settings;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Pippin.Domain;
using Pippin.Parsing;
using Pippin.State;
using Pippin.ViewModels;
using Pippin.Views;

namespace Pippin;

public sealed class PippinModule : IGorgonModule
{
    public string Id => "pippin";
    public string DisplayName => "Pippin · Gourmand";
    public PackIconLucideKind Icon => PackIconLucideKind.UtensilsCrossed;
    public string? IconUri => "pack://application:,,,/Pippin.Module;component/Resources/pippin.ico";
    public int SortOrder => 150;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(GourmandView);
    public Type? SettingsViewType => typeof(PippinSettingsView);

    public void Register(IServiceCollection services)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pippinDir = Path.Combine(localApp, "Gorgon", "Pippin");
        var statePath = Path.Combine(pippinDir, "gourmand-state.json");

        // Persistence
        services.AddSingleton<ISettingsStore<GourmandState>>(_ =>
            new JsonSettingsStore<GourmandState>(statePath, GourmandStateJsonContext.Default.GourmandState));

        // Domain
        services.AddSingleton<FoodCatalog>(sp =>
            new FoodCatalog(sp.GetRequiredService<IReferenceDataService>()));

        // Parsing
        services.AddSingleton<GourmandLogParser>();

        // State
        services.AddSingleton<GourmandStateMachine>();
        services.AddSingleton<GourmandStateService>();

        // ViewModel + View
        services.AddSingleton<GourmandViewModel>(sp => new GourmandViewModel(
            sp.GetRequiredService<GourmandStateMachine>(),
            sp.GetRequiredService<FoodCatalog>(),
            sp.GetService<ICharacterDataService>()));
        services.AddSingleton<GourmandView>(sp => new GourmandView
        {
            DataContext = sp.GetRequiredService<GourmandViewModel>(),
        });
        services.AddSingleton<PippinSettingsView>(_ => new PippinSettingsView());

        // Background ingestion
        services.AddHostedService<GourmandIngestionService>();
    }
}
