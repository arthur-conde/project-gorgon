using System.Text.Json;
using Arwen.Domain;
using FluentAssertions;
using Xunit;

namespace Arwen.Tests;

public sealed class ArwenSettingsTests
{
    [Fact]
    public void PendingObservationTtl_RoundTripsThroughJson()
    {
        var original = new ArwenSettings
        {
            PendingObservationTtl = TimeSpan.FromHours(6),
        };

        var json = JsonSerializer.Serialize(original, ArwenJsonContext.Default.ArwenSettings);
        var roundTripped = JsonSerializer.Deserialize(json, ArwenJsonContext.Default.ArwenSettings);

        roundTripped.Should().NotBeNull();
        roundTripped!.PendingObservationTtl.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void PendingObservationTtl_DefaultsTo24Hours()
    {
        new ArwenSettings().PendingObservationTtl.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void PendingObservationTtl_SetterRaisesPropertyChanged()
    {
        var settings = new ArwenSettings();
        var changed = false;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArwenSettings.PendingObservationTtl)) changed = true;
        };

        settings.PendingObservationTtl = TimeSpan.FromHours(1);

        changed.Should().BeTrue();
    }
}
