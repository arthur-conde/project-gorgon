using Mithril.Shared.Wpf.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace Mithril.Shared.Wpf.DependencyInjection;

public static class WpfServiceCollectionExtensions
{
    public static IServiceCollection AddMithrilDialogs(this IServiceCollection services) =>
        services.AddSingleton<IDialogService, DialogService>();
}
