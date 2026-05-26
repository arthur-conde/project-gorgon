using System.IO;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.Shared.Character;
using Saruman.State;
using Xunit;

namespace Saruman.Tests.State;

[Collection("FileIO")]
public sealed class SarumanCodebookServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;
    private readonly TestDomainEventBus _bus;
    private readonly PerCharacterStore<SarumanCodebook> _store;
    private readonly PerCharacterView<SarumanCodebook> _view;
    private readonly SarumanCodebookService _service;

    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    public SarumanCodebookServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-codebook");
        _active = new FakeActiveCharacterService();
        _bus = new TestDomainEventBus();
        _store = new PerCharacterStore<SarumanCodebook>(
            _root, "saruman-codebook.json", SarumanCodebookJsonContext.Default.SarumanCodebook);
        _view = new PerCharacterView<SarumanCodebook>(_active, _store);
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
        _service = new SarumanCodebookService(_view, _bus);
    }

    public void Dispose()
    {
        _service.Dispose();
        _view.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Discovery_AddsEntry_AndPersists()
    {
        Discover("CHUCKMRYJ", "Fast Swimmer", "Swim faster!");

        _service.Entries.Should().ContainKey("CHUCKMRYJ");
        var entry = _service.Entries["CHUCKMRYJ"];
        entry.Effect.Should().Be("Fast Swimmer");
        entry.Description.Should().Be("Swim faster!");
        entry.LastSpentAt.Should().BeNull();

        // Verify persistence: reload from disk
        var reloaded = _store.Load("Arthur", "Kwatoxi");
        reloaded.Entries.Should().ContainKey("CHUCKMRYJ");
        reloaded.Entries["CHUCKMRYJ"].Effect.Should().Be("Fast Swimmer");
    }

    [Fact]
    public void Discovery_IsDeduplicated()
    {
        var changed = 0;
        _service.CodebookChanged += (_, _) => changed++;

        Discover("ABCDEF", "Effect1", "Desc");
        Discover("ABCDEF", "Effect2", "Desc2");

        _service.Entries.Should().HaveCount(1);
        _service.Entries["ABCDEF"].Effect.Should().Be("Effect1");
        changed.Should().Be(1);
    }

    [Fact]
    public void ChatLine_FlipsSpentState_WhenCodeMatches()
    {
        Discover("BWUBGUCH", "Anemia", "");

        _service.Entries["BWUBGUCH"].LastSpentAt.Should().BeNull();

        Chat("Arthur", "[Global] Arthur: I just used BWUBGUCH in combat!");

        _service.Entries["BWUBGUCH"].LastSpentAt.Should().NotBeNull();
    }

    [Fact]
    public void ChatLine_IgnoresTokens_NotInCodebook()
    {
        Discover("REALCODE", "Effect", "");

        Chat("Arthur", "[Global] Arthur: FAKECODE is not real");

        _service.Entries.Should().NotContainKey("FAKECODE");
        _service.Entries["REALCODE"].LastSpentAt.Should().BeNull();
    }

    [Fact]
    public void ChatLine_SpentIsMonotonic()
    {
        Discover("MYCODE", "Effect", "");
        Chat("Arthur", "[Global] Arthur: MYCODE");

        var firstSpent = _service.Entries["MYCODE"].LastSpentAt;
        firstSpent.Should().NotBeNull();

        Chat("Arthur", "[Global] Arthur: MYCODE again");

        _service.Entries["MYCODE"].LastSpentAt.Should().Be(firstSpent);
    }

    [Fact]
    public void ChatLine_RequiresMinLength4()
    {
        Discover("ABC", "Short", "");

        Chat("Arthur", "[Global] Arthur: ABC is too short");

        _service.Entries["ABC"].LastSpentAt.Should().BeNull();
    }

    [Fact]
    public void ChatLine_PersistsSpentState()
    {
        Discover("PERSIST", "Effect", "");
        Chat("Arthur", "[Global] Arthur: PERSIST");

        var reloaded = _store.Load("Arthur", "Kwatoxi");
        reloaded.Entries["PERSIST"].LastSpentAt.Should().NotBeNull();
    }

    [Fact]
    public void CharacterSwitch_LoadsSeparateCodebook()
    {
        Discover("CHARCODE", "Effect", "");

        _active.SetActiveCharacter("Bilbo", "Kwatoxi");

        _service.Entries.Should().NotContainKey("CHARCODE");
    }

    [Fact]
    public void CodebookChanged_FiresOnDiscoveryAndSpend()
    {
        var events = new List<string>();
        _service.CodebookChanged += (_, _) => events.Add("changed");

        Discover("TESTCODE", "Effect", "");
        events.Should().HaveCount(1);

        Chat("Arthur", "[Global] Arthur: TESTCODE");
        events.Should().HaveCount(2);
    }

    private void Discover(string code, string effect, string desc)
    {
        _bus.Publish(new WordOfPowerDiscovered(
            code.AsMemory(),
            effect.AsMemory(),
            desc.AsMemory(),
            new LogLineMetadata(BaseTime, BaseTime, IsReplay: false)));
    }

    private void Chat(string speaker, string fullLine)
    {
        var closeBracket = fullLine.IndexOf(']');
        var channel = fullLine[1..closeBracket];
        var afterChannel = fullLine[(closeBracket + 2)..];
        var colonSpace = afterChannel.IndexOf(": ");
        var text = afterChannel[(colonSpace + 2)..];

        _bus.Publish(new PlayerChatLine(
            channel, speaker, text,
            new LogLineMetadata(BaseTime.AddSeconds(1), BaseTime.AddSeconds(1), IsReplay: false)));
    }
}

internal sealed class TestDomainEventBus : IDomainEventSubscriber
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryGetValue(typeof(T), out var list))
            _handlers[typeof(T)] = list = [];
        list.Add(handler);
        return new Sub(() => list.Remove(handler));
    }

    public void Publish<T>(T evt) where T : struct
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        foreach (var h in list.ToArray())
            ((Action<T>)h)(evt);
    }

    private sealed class Sub(Action onDispose) : IDisposable
    {
        private Action? _action = onDispose;
        public void Dispose() { _action?.Invoke(); _action = null; }
    }
}
