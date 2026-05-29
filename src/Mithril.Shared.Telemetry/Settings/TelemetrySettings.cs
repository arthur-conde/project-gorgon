using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Mithril.Shared.Telemetry.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<OtlpProtocol>))]
public enum OtlpProtocol { Grpc, HttpProtobuf }

/// <summary>
/// Persisted opt-in OTLP export configuration. Mutations are surfaced via
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
/// (<c>NotifyPropertyChangedOptionsMonitor</c>) which fires <c>OnChange</c> on
/// every in-place edit. <see cref="TagExports"/> is read live per exported
/// record so chip toggles apply without restart. <see cref="Endpoint"/>,
/// <see cref="Headers"/>, <see cref="Protocol"/>, and <see cref="ServiceName"/>
/// are <strong>restart-required</strong>: the OTel SDK bakes them into the
/// exporter instance at provider-build time and never re-reads (mithril#833).
/// <see cref="EnableOtlpExport"/> also requires a restart so off-mode preserves
/// zero <see cref="System.Diagnostics.ActivitySource.HasListeners"/> cost.
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
        // The stamp is redundant when invoked via PerCharacterStore.RunMigrate (which bumps
        // SchemaVersion after Migrate returns) but is required for direct callers (unit tests).
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

    private bool _trustEndpoint;
    /// <summary>
    /// When <c>true</c>, span exports skip the allowlist gate (<see cref="Catalog.TagCatalog"/>
    /// membership + <see cref="TagExports"/> overrides + Sensitive-by-default drops) so
    /// every producer tag flows to the OTLP destination. Use only for fully-trusted local
    /// destinations (e.g. a Seq container the user runs themselves). The
    /// <c>ValueRedactor</c> still runs as belt-and-suspenders against accidental paths /
    /// character-name leaks in string tag values. Restart-required: the processor choice
    /// is captured once in <c>AddMithrilOtlpExport</c>. Off by default. mithril#840.
    /// </summary>
    public bool TrustEndpoint { get => _trustEndpoint; set => Set(ref _trustEndpoint, value); }

    /// <summary>Header NAME → wrapped value. Wrap on set via UI VM; unwrap when binding to OTel options.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Tag-key → exported-or-not user override. Absent key means "use catalog default".
    ///
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> because three threads touch this:
    /// the UI thread (chip-toggle writer), the OTel BatchProcessor consumer thread
    /// (per-tag <c>TryGetValue</c> reader in <c>AllowlistAndRedactionProcessor.OnEnd</c>),
    /// and the <c>SettingsAutoSaver</c> timer thread (JSON-serialise reader). A plain
    /// <see cref="Dictionary{TKey,TValue}"/> would risk corruption during rehash under
    /// concurrent write+read.
    /// </summary>
    public ConcurrentDictionary<string, bool> TagExports { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Raise <see cref="PropertyChanged"/> for callers that mutated a collection
    /// in-place (e.g. <see cref="Headers"/> / <see cref="TagExports"/> add or
    /// remove). The settings VM calls this after dictionary edits so
    /// <c>SettingsAutoSaver&lt;T&gt;</c> picks them up. Modelled on
    /// CelebrimborSettings.Touch.
    /// </summary>
    public void Touch([CallerMemberName] string? name = null)
    {
        if (!string.IsNullOrEmpty(name))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TelemetrySettings))]
public partial class TelemetrySettingsJsonContext : JsonSerializerContext { }
