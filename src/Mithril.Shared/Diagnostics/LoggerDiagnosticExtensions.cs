using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Mithril.Shared.Diagnostics;

/// <summary>
/// Bridges legacy category+message diagnostics calls to MEL structured logging
/// consumed by <see cref="DiagnosticsLoggerProvider"/>.
/// </summary>
public static class LoggerDiagnosticExtensions
{
    public static void LogDiagnostic(
        this ILogger? logger,
        MelLogLevel level,
        string category,
        string message)
    {
        if (logger is null) return;
        logger.Log(level, "{Category} {Detail}", category, message);
    }

    public static void LogDiagnosticTrace(this ILogger? logger, string category, string message) =>
        logger.LogDiagnostic(MelLogLevel.Trace, category, message);

    public static void LogDiagnosticInfo(this ILogger? logger, string category, string message) =>
        logger.LogDiagnostic(MelLogLevel.Information, category, message);

    public static void LogDiagnosticWarn(this ILogger? logger, string category, string message) =>
        logger.LogDiagnostic(MelLogLevel.Warning, category, message);

    public static void LogDiagnosticError(this ILogger? logger, string category, string message) =>
        logger.LogDiagnostic(MelLogLevel.Error, category, message);
}
