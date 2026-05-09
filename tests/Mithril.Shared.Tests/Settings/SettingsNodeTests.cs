using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using FluentAssertions;
using Mithril.Shared.Settings;
using Xunit;

namespace Mithril.Shared.Tests.Settings;

/// <summary>
/// Regression net for #179. SettingsAutoSaver subscribes only to the root
/// instance's PropertyChanged; nested INPC children must bubble through
/// SettingsNode.Bubble or their mutations are silently dropped on autosave.
/// </summary>
public class SettingsNodeTests
{
    [Fact]
    public void Child_PropertyChanged_bubbles_to_parent_after_Bubble()
    {
        var parent = new TestParent();
        var child = new TestChild();
        parent.Adopt(child);

        var fired = 0;
        parent.PropertyChanged += (_, _) => fired++;

        child.Value = "changed";

        fired.Should().Be(1, "Bubble must re-fire the child's PropertyChanged on the parent");
    }

    [Fact]
    public void Child_PropertyChanged_does_not_bubble_after_Unbubble()
    {
        var parent = new TestParent();
        var child = new TestChild();
        parent.Adopt(child);
        parent.Disown(child);

        var fired = 0;
        parent.PropertyChanged += (_, _) => fired++;

        child.Value = "changed";

        fired.Should().Be(0);
    }

    [Fact]
    public void Multiple_children_each_bubble_independently()
    {
        var parent = new TestParent();
        var a = new TestChild();
        var b = new TestChild();
        parent.Adopt(a);
        parent.Adopt(b);

        var fired = 0;
        parent.PropertyChanged += (_, _) => fired++;

        a.Value = "x";
        b.Value = "y";

        fired.Should().Be(2);
    }

    [Fact]
    public void PostLoadInit_rewires_bubbling_for_deserialized_children()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mithril-settingsnode-test-{Guid.NewGuid():N}.json");
        try
        {
            // Seed a parent with one child, save through the store.
            var seed = new TestRoot { Child = new TestChild { Value = "initial" } };
            seed.PostLoadInit(); // wire bubbling on the seed instance
            var store = new JsonSettingsStore<TestRoot>(tempPath, TestRootJsonContext.Default.TestRoot);
            store.Save(seed);

            // Reload — STJ source-gen will populate Child via the property
            // setter, NOT through ctor wiring. Without IPostLoadInit being
            // invoked by the store, the loaded child's mutations would fire
            // on the child only.
            var loaded = store.Load();

            var fired = 0;
            loaded.PropertyChanged += (_, _) => fired++;

            loaded.Child!.Value = "after-load";

            fired.Should().Be(1, "IPostLoadInit must re-wire bubbling on deserialized children");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}

public sealed class TestParent : SettingsNode
{
    public void Adopt(INotifyPropertyChanged child) => Bubble(child);
    public void Disown(INotifyPropertyChanged child) => Unbubble(child);
}

public sealed class TestChild : SettingsNode
{
    private string _value = "";
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed class TestRoot : SettingsNode, IPostLoadInit
{
    public TestChild? Child { get; set; }

    public void PostLoadInit()
    {
        if (Child is not null) Bubble(Child);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestRoot))]
public partial class TestRootJsonContext : JsonSerializerContext;
