using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics;
using Xunit;

namespace Mithril.Shared.Tests.Diagnostics;

/// <summary>
/// Regression test for the bug where AddMithrilLogging registered DiagnosticsLoggerProvider
/// as ILoggerProvider BEFORE calling builder.ClearProviders(), which silently wiped the
/// registration. Production MEL loggers were no-ops, and the diagnostics view showed nothing.
/// </summary>
public sealed class DiagnosticsLoggingDiPipelineTests
{
    [Fact]
    public void Mel_logger_resolved_from_DI_surfaces_in_IDiagnosticsLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-di-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddMithrilLogging(dir);
            using var sp = services.BuildServiceProvider();

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CategoryX");
            logger.LogInformation("hello pipeline");

            var diag = sp.GetRequiredService<IDiagnosticsLog>();
            diag.Snapshot().Should().ContainSingle(e =>
                e.Category == "CategoryX"
                && e.Message == "hello pipeline"
                && e.Level == DiagnosticLevel.Info);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
