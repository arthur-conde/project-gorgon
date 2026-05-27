using Mithril.Shared.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Mithril.Shared.DependencyInjection;

public static class DiagnosticsLoggingExtensions
{
    /// <summary>
    /// Registers unified Mithril logging: <see cref="DiagnosticsLoggerProvider"/>
    /// (ring buffer, Rx live stream, Serilog compact-JSON file).
    /// </summary>
    public static IServiceCollection AddMithrilLogging(this IServiceCollection services, string logDirectory)
    {
        services.AddSingleton<DiagnosticsLoggerProvider>(_ => new DiagnosticsLoggerProvider(logDirectory));
        services.AddSingleton<IDiagnosticsLog>(sp => sp.GetRequiredService<DiagnosticsLoggerProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, DiagnosticsLoggerProvider>(sp =>
            sp.GetRequiredService<DiagnosticsLoggerProvider>()));

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(MelLogLevel.Trace);
        });

        return services;
    }
}
