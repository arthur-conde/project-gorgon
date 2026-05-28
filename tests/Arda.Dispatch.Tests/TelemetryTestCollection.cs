using Xunit;

namespace Arda.Dispatch.Tests;

/// <summary>
/// Tests that subscribe global <see cref="System.Diagnostics.ActivityListener"/> or
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> instances share a process-wide
/// listener registry — running them in parallel causes one test's listener to capture
/// another test's emissions. This collection forces serial execution for tests that
/// exercise the BCL telemetry primitives directly.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTestCollection
{
    public const string Name = "Telemetry";
}
