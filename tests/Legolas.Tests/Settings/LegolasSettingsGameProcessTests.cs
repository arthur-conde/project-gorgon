using System.ComponentModel;
using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Settings;

public class LegolasSettingsGameProcessTests
{
    [Fact]
    public void GameProcessName_default_is_ProjectGorgon()
    {
        new LegolasSettings().GameProcessName.Should().Be("ProjectGorgon");
    }

    [Fact]
    public void GameProcessName_trims_whitespace_on_set()
    {
        var s = new LegolasSettings();
        s.GameProcessName = "  PG_x64  ";
        s.GameProcessName.Should().Be("PG_x64");
    }

    [Fact]
    public void GameProcessName_preserves_internal_whitespace()
    {
        // Substring match must still work for launchers that name the
        // executable with a space, so internal spaces are preserved.
        var s = new LegolasSettings();
        s.GameProcessName = "Project Gorgon";
        s.GameProcessName.Should().Be("Project Gorgon");
    }

    [Fact]
    public void GameProcessName_null_becomes_empty()
    {
        var s = new LegolasSettings();
        s.GameProcessName = null!;
        s.GameProcessName.Should().Be(string.Empty);
    }

    [Fact]
    public void GameProcessName_raises_PropertyChanged_on_change()
    {
        var s = new LegolasSettings();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)s).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        s.GameProcessName = "Different";

        changed.Should().Contain(nameof(LegolasSettings.GameProcessName));
    }

    [Fact]
    public void GameProcessName_does_not_raise_PropertyChanged_when_unchanged()
    {
        var s = new LegolasSettings { GameProcessName = "ProjectGorgon" };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)s).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        s.GameProcessName = "ProjectGorgon";
        s.GameProcessName = "  ProjectGorgon  "; // post-trim equal to existing

        changed.Should().NotContain(nameof(LegolasSettings.GameProcessName));
    }
}
