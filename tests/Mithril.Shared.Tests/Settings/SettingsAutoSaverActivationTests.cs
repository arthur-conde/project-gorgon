using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Xunit;

namespace Mithril.Shared.Tests.Settings;

/// <summary>
/// Regression net for #101. The bug class: a module registers
/// <see cref="SettingsAutoSaver{T}"/> as a singleton but nothing in the DI
/// graph forces it to construct, so it never subscribes to PropertyChanged
/// and settings silently fail to persist across restarts.
///
/// The helper <see cref="ServiceCollectionExtensions.AddMithrilSettings{T}"/>
/// closes the gap by also registering the saver as an <see cref="IHostedService"/>.
/// Generic Host eagerly activates every IHostedService, so the saver always
/// constructs at startup. These tests assert that contract.
/// </summary>
public class SettingsAutoSaverActivationTests
{
    /// <summary>
    /// Every module under test. Statically referenced so the assemblies are
    /// loaded by the time xunit collects MemberData (AppDomain enumeration
    /// doesn't see lazy-loaded module assemblies).
    /// </summary>
    public static IEnumerable<object[]> AllModules() => new IMithrilModule[]
    {
        new Samwise.SamwiseModule(),
        new Arwen.ArwenModule(),
        new Bilbo.BilboModule(),
        new Celebrimbor.CelebrimborModule(),
        new Elrond.ElrondModule(),
        new Gandalf.GandalfModule(),
        new Legolas.LegolasModule(),
        new Smaug.SmaugModule(),
        new Pippin.PippinModule(),
        new Saruman.SarumanModule(),
    }.Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Module_pairs_every_SettingsAutoSaver_with_an_IHostedService(IMithrilModule module)
    {
        var services = new ServiceCollection();
        module.Register(services);

        var saverTypes = services
            .Where(d => d.ServiceType.IsGenericType
                     && d.ServiceType.GetGenericTypeDefinition() == typeof(SettingsAutoSaver<>))
            .Select(d => d.ServiceType)
            .Distinct()
            .ToList();

        if (saverTypes.Count == 0) return;

        var hostedFactories = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null)
            .Select(d => d.ImplementationFactory!)
            .ToList();

        foreach (var saverType in saverTypes)
        {
            var settingsType = saverType.GetGenericArguments()[0];
            var storeType = typeof(ISettingsStore<>).MakeGenericType(settingsType);

            // Build a minimal sub-provider with just the saver's own dependencies.
            // Avoids dragging in unrelated module deps (HttpClient, log streams, etc.).
            IServiceCollection sub = new ServiceCollection();
            foreach (var d in services)
            {
                if (d.ServiceType == saverType
                 || d.ServiceType == settingsType
                 || d.ServiceType == storeType)
                {
                    sub.Add(d);
                }
            }

            using var sp = sub.BuildServiceProvider();
            var saver = sp.GetRequiredService(saverType);

            // Probe each hosted-service factory. The matching one is whichever
            // returns the saver instance when handed the same sub-provider.
            var matched = hostedFactories.Any(f =>
            {
                try { return ReferenceEquals(f(sp), saver); }
                catch { return false; }
            });

            matched.Should().BeTrue(
                $"{module.GetType().Name} registers {saverType.Name} as a singleton but " +
                $"nothing forces it to activate. Use AddMithrilSettings<{settingsType.Name}> " +
                $"so the saver is also registered as an IHostedService.");
        }
    }

    [Fact]
    public void AddMithrilSettings_registers_store_instance_saver_and_hosted_service()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mithril-test-{Guid.NewGuid():N}.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMithrilSettings<FakeSettings>(tempPath, FakeSettingsJsonContext.Default.FakeSettings);

            using var sp = services.BuildServiceProvider();

            sp.GetRequiredService<ISettingsStore<FakeSettings>>().Should().NotBeNull();
            sp.GetRequiredService<FakeSettings>().Should().NotBeNull();

            var saver = sp.GetRequiredService<SettingsAutoSaver<FakeSettings>>();
            saver.Should().NotBeNull();

            // The hosted service registration MUST resolve to the same instance —
            // otherwise StopAsync's flush hits a different saver than the one the
            // settings instance is subscribed to.
            var hosted = sp.GetServices<IHostedService>().OfType<SettingsAutoSaver<FakeSettings>>().Single();
            hosted.Should().BeSameAs(saver);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}

public sealed class FakeSettings : INotifyPropertyChanged
{
    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

[System.Text.Json.Serialization.JsonSerializable(typeof(FakeSettings))]
public partial class FakeSettingsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
