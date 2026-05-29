using System;
using System.ComponentModel;
using Microsoft.Extensions.Options;

namespace Mithril.Shared.Telemetry.Settings;

/// <summary>
/// <see cref="IOptionsMonitor{T}"/> adapter over a settings singleton that
/// raises <see cref="INotifyPropertyChanged.PropertyChanged"/> on mutation
/// (Mithril's <c>ISettingsStore&lt;T&gt;</c>-backed settings types — see
/// <see cref="TelemetrySettings"/>). Two responsibilities:
/// <list type="number">
/// <item><see cref="CurrentValue"/> / <see cref="Get"/> always return the
///   wrapped singleton, so existing per-record live reads
///   (<c>CurrentValue.TagExports</c> in the scrubber processors) keep
///   reflecting in-place dictionary edits exactly as they did under the prior
///   <c>SingletonOptionsMonitor</c> shim.</item>
/// <item><see cref="OnChange"/> registrations actually fire — every
///   <c>PropertyChanged</c> on the singleton (the settings UI's in-place
///   mutation + <c>Touch()</c> path, see <c>TelemetrySettingsViewModel</c>)
///   invokes registered listeners. The prior shim's <c>OnChange</c> was a
///   no-op, so subscribers silently never heard about changes (mithril#833).</item>
/// </list>
///
/// <para><strong>Scope of "hot-reload" delivered (mithril#833).</strong>
/// Firing <c>OnChange</c> lets UI / future consumers react to settings edits
/// live. It does <em>not</em> make the OTLP exporter pick up endpoint / headers
/// / protocol / service-name changes without a restart: OTel SDK 1.15.x bakes
/// those into the exporter instance at provider-build time (the
/// <c>AddOtlpExporter(Action&lt;OtlpExporterOptions&gt;)</c> callback runs once;
/// <c>OtlpTraceExporter</c> snapshots them into its transmission handler in its
/// constructor and never re-reads). A per-export <c>IServiceProvider</c> factory
/// overload that would allow live re-reads is an open upstream request
/// (open-telemetry/opentelemetry-dotnet#6537). Live endpoint/header swapping
/// therefore requires a provider rebuild — tracked as a follow-up.</para>
///
/// <para>No file watching: Mithril's <c>ISettingsStore&lt;T&gt;</c> does not
/// observe the persisted JSON, so out-of-band file edits are not surfaced here
/// (they aren't anywhere in the settings stack). The live signal is the
/// in-process singleton mutation, which is what the settings UI performs.</para>
/// </summary>
public sealed class NotifyPropertyChangedOptionsMonitor<T> : IOptionsMonitor<T>, IDisposable
    where T : class, INotifyPropertyChanged
{
    private readonly T _instance;
    private event Action<T, string?>? _listeners;

    public NotifyPropertyChangedOptionsMonitor(T instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instance = instance;
        _instance.PropertyChanged += OnPropertyChanged;
    }

    public T CurrentValue => _instance;

    public T Get(string? name) => _instance;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listeners += listener;
        return new Subscription(this, listener);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IOptionsMonitor.OnChange listeners receive the value + the named
        // options instance (null for the default unnamed options). We forward
        // the changed property name in that slot so a listener can filter on
        // it if it wants finer granularity, while still working for listeners
        // that ignore the name.
        _listeners?.Invoke(_instance, e.PropertyName);
    }

    public void Dispose()
    {
        _instance.PropertyChanged -= OnPropertyChanged;
        _listeners = null;
    }

    private sealed class Subscription(NotifyPropertyChangedOptionsMonitor<T> owner, Action<T, string?> listener)
        : IDisposable
    {
        private NotifyPropertyChangedOptionsMonitor<T>? _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null) return;
            _owner = null;
            owner._listeners -= listener;
        }
    }
}
