using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.Shared.Hotkeys;
using Xunit;

namespace Mithril.Shared.Tests;

public class HotkeyServiceExecuteCommandTests
{
    private static HotkeyService BuildService(ILogger<HotkeyService>? logger = null)
    {
        var registry = new HotkeyRegistry([]);
        var gate = new AlwaysOpenHotkeyGate();
        return new HotkeyService(registry, gate, logger ?? NullLogger<HotkeyService>.Instance);
    }

    [Fact]
    public async Task ExecuteCommandSafely_ThrowingCommand_LogsWarningWithException()
    {
        var recorder = new RecordingLogger();
        var svc = BuildService(recorder);
        var boom = new ThrowingCommand("throw-cmd", new InvalidOperationException("boom"));

        await svc.ExecuteCommandSafelyAsync(boom);

        recorder.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning)
            .Which.Exception.Should().Be(boom.ThrowException);
    }

    [Fact]
    public async Task ExecuteCommandSafely_ThrowingCommand_LogsCommandId()
    {
        var recorder = new RecordingLogger();
        var svc = BuildService(recorder);
        var boom = new ThrowingCommand("my-cmd-id", new InvalidOperationException("boom"));

        await svc.ExecuteCommandSafelyAsync(boom);

        recorder.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning)
            .Which.State.Should().Contain(p => p.Key == "CommandId" && Equals(p.Value, "my-cmd-id"));
    }

    [Fact]
    public async Task ExecuteCommandSafely_ThrowingThenSuccess_PumpSurvives()
    {
        var svc = BuildService();
        var boom = new ThrowingCommand("fail", new InvalidOperationException("oops"));
        var ok = new TrackingCommand("ok");

        await svc.ExecuteCommandSafelyAsync(boom);
        await svc.ExecuteCommandSafelyAsync(ok);

        ok.WasExecuted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteCommandSafely_NonThrowingCommand_NoWarningLogged()
    {
        var recorder = new RecordingLogger();
        var svc = BuildService(recorder);
        var ok = new TrackingCommand("ok");

        await svc.ExecuteCommandSafelyAsync(ok);

        recorder.Records.Should().BeEmpty();
    }

    // --- fakes ---

    private sealed class ThrowingCommand(string id, Exception throwException) : IHotkeyCommand
    {
        public Exception ThrowException { get; } = throwException;
        public string Id => id;
        public string DisplayName => id;
        public string? Category => null;
        public HotkeyBinding? DefaultBinding => null;
        public bool RespectsFocusGate => false;
        public Task ExecuteAsync(CancellationToken ct) => throw ThrowException;
    }

    private sealed class TrackingCommand(string id) : IHotkeyCommand
    {
        public bool WasExecuted { get; private set; }
        public string Id => id;
        public string DisplayName => id;
        public string? Category => null;
        public HotkeyBinding? DefaultBinding => null;
        public bool RespectsFocusGate => false;
        public Task ExecuteAsync(CancellationToken ct) { WasExecuted = true; return Task.CompletedTask; }
    }

    // --- recording logger (mirrors DomainEventBusTests.RecordingLogger) ---

    private sealed class RecordingLogger : ILogger<HotkeyService>
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? [new KeyValuePair<string, object?>("{OriginalFormat}", formatter(state, exception))];
            Records.Add(new LogRecord(logLevel, pairs.ToList(), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed record LogRecord(
        LogLevel Level,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        Exception? Exception);
}
