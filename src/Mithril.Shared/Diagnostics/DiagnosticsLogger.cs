using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Mithril.Shared.Diagnostics;

internal sealed class DiagnosticsLogger : ILogger
{
    private readonly DiagnosticsLoggerProvider _provider;
    private readonly string _category;

    public DiagnosticsLogger(DiagnosticsLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(MelLogLevel logLevel) => logLevel != MelLogLevel.None;

    public void Log<TState>(
        MelLogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var category = _category;
        var message = formatter(state, exception);

        if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
        {
            foreach (var kv in structured)
            {
                if (kv.Key == "Category" && kv.Value is string cat)
                    category = cat;
                else if (kv.Key == "Detail" && kv.Value is string detail)
                    message = detail;
            }
        }

        if (exception is not null)
            message = string.IsNullOrEmpty(message) ? exception.ToString() : $"{message} {exception}";

        _provider.Publish(logLevel, category, message);
    }
}
