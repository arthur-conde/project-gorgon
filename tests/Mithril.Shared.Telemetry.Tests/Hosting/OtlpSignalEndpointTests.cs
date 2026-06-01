using FluentAssertions;
using Mithril.Shared.Telemetry.Hosting;
using Mithril.Shared.Telemetry.Settings;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Hosting;

/// <summary>
/// mithril#968 — the OTLP exporter must route each signal to its own
/// <c>v1/{traces|metrics|logs}</c> path. The per-signal <c>AddOtlpExporter</c>
/// path Mithril uses takes the configured endpoint <em>verbatim</em> (the SDK's
/// <c>AppendSignalPathToEndpoint</c> is internal and forced off once
/// <c>Endpoint</c> is set programmatically), so Mithril derives the path itself.
/// </summary>
public class OtlpSignalEndpointTests
{
    [Theory]
    [InlineData("traces", "http://localhost:5341/ingest/otlp/v1/traces")]
    [InlineData("metrics", "http://localhost:5341/ingest/otlp/v1/metrics")]
    [InlineData("logs", "http://localhost:5341/ingest/otlp/v1/logs")]
    public void Base_endpoint_gets_per_signal_path_appended(string signal, string expected)
    {
        var resolved = TelemetryHostExtensions.ResolveSignalEndpoint(
            "http://localhost:5341/ingest/otlp", OtlpProtocol.HttpProtobuf, signal);

        resolved!.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("traces", "http://localhost:5341/ingest/otlp/v1/traces")]
    [InlineData("metrics", "http://localhost:5341/ingest/otlp/v1/metrics")]
    [InlineData("logs", "http://localhost:5341/ingest/otlp/v1/logs")]
    public void Pasted_traces_url_is_re_derived_per_signal(string signal, string expected)
    {
        // The common Seq mistake: the user pastes the traces URL into the single
        // Endpoint field. metrics/logs must NOT inherit the /v1/traces path.
        var resolved = TelemetryHostExtensions.ResolveSignalEndpoint(
            "http://localhost:5341/ingest/otlp/v1/traces", OtlpProtocol.HttpProtobuf, signal);

        resolved!.ToString().Should().Be(expected);
    }

    [Fact]
    public void Any_pasted_signal_path_is_stripped_before_re_deriving()
    {
        // A pasted metrics or logs URL re-derives just like a pasted traces URL.
        TelemetryHostExtensions.ResolveSignalEndpoint(
                "http://localhost:5341/ingest/otlp/v1/metrics", OtlpProtocol.HttpProtobuf, "traces")!
            .ToString().Should().Be("http://localhost:5341/ingest/otlp/v1/traces");

        TelemetryHostExtensions.ResolveSignalEndpoint(
                "http://localhost:5341/ingest/otlp/v1/logs", OtlpProtocol.HttpProtobuf, "logs")!
            .ToString().Should().Be("http://localhost:5341/ingest/otlp/v1/logs");
    }

    [Fact]
    public void Trailing_slash_on_base_is_tolerated()
    {
        TelemetryHostExtensions.ResolveSignalEndpoint(
                "http://localhost:5341/ingest/otlp/", OtlpProtocol.HttpProtobuf, "logs")!
            .ToString().Should().Be("http://localhost:5341/ingest/otlp/v1/logs");
    }

    [Fact]
    public void Existing_signal_path_strip_is_case_insensitive()
    {
        TelemetryHostExtensions.ResolveSignalEndpoint(
                "http://localhost:5341/ingest/otlp/v1/TRACES", OtlpProtocol.HttpProtobuf, "metrics")!
            .ToString().Should().Be("http://localhost:5341/ingest/otlp/v1/metrics");
    }

    [Fact]
    public void Root_endpoint_gets_signal_path()
    {
        // The OTLP/HTTP default-style endpoint with no ingest sub-path.
        TelemetryHostExtensions.ResolveSignalEndpoint(
                "http://localhost:4318", OtlpProtocol.HttpProtobuf, "traces")!
            .ToString().Should().Be("http://localhost:4318/v1/traces");
    }

    [Theory]
    [InlineData("traces")]
    [InlineData("metrics")]
    [InlineData("logs")]
    public void Grpc_endpoint_is_used_verbatim(string signal)
    {
        // gRPC routes the signal via the service method, not a URL path. The
        // endpoint must be used as-is for every signal — appending /v1/{signal}
        // would break gRPC export.
        TelemetryHostExtensions.ResolveSignalEndpoint(
                "http://localhost:4317", OtlpProtocol.Grpc, signal)!
            .ToString().Should().Be("http://localhost:4317/");
    }

    [Fact]
    public void Unparseable_endpoint_returns_null_so_caller_keeps_the_sdk_default()
    {
        TelemetryHostExtensions.ResolveSignalEndpoint(
            "not a url", OtlpProtocol.HttpProtobuf, "traces").Should().BeNull();
        TelemetryHostExtensions.ResolveSignalEndpoint(
            null, OtlpProtocol.HttpProtobuf, "traces").Should().BeNull();
    }
}
