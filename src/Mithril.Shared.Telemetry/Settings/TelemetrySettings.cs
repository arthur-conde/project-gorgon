using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Mithril.Shared.Telemetry.Settings;

public enum OtlpProtocol { Grpc, HttpProtobuf }

/// <summary>
/// Persisted opt-in OTLP export configuration. Hot-reloaded via
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/> for
/// endpoint/headers/scrubber changes; <see cref="EnableOtlpExport"/>
/// requires a restart so off-mode preserves zero
/// <see cref="System.Diagnostics.ActivitySource.HasListeners"/> cost.
///
/// Header values are DPAPI-wrapped at rest via
/// <see cref="HeaderValueProtection"/>. Round-trip is transparent — the
/// wrap prefix lets <see cref="HeaderValueProtection.Unprotect"/> handle
/// plaintext values gracefully if the file is hand-edited.
/// </summary>
public sealed class TelemetrySettings : INotifyPropertyChanged, IVersionedState<TelemetrySettings>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;

    public static TelemetrySettings Migrate(TelemetrySettings loaded)
    {
        // v0 -> v1: no field renames yet; bump version stamp.
        loaded.SchemaVersion = Version;
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;

    private bool _enableOtlpExport;
    public bool EnableOtlpExport { get => _enableOtlpExport; set => Set(ref _enableOtlpExport, value); }

    private string _endpoint = "http://localhost:5341/ingest/otlp/v1/traces";
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }

    private OtlpProtocol _protocol = OtlpProtocol.HttpProtobuf;
    public OtlpProtocol Protocol { get => _protocol; set => Set(ref _protocol, value); }

    private string _serviceName = "mithril";
    public string ServiceName { get => _serviceName; set => Set(ref _serviceName, value); }

    /// <summary>Header NAME → wrapped value. Wrap on set via UI VM; unwrap when binding to OTel options.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>Tag-key → exported-or-not user override. Absent key means "use catalog default".</summary>
    public Dictionary<string, bool> TagExports { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TelemetrySettings))]
public partial class TelemetrySettingsJsonContext : JsonSerializerContext { }
