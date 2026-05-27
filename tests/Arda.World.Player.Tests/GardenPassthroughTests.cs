using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class GardenPassthroughTests
{
    private readonly SpyEventBus _bus = new();

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    // ── UpdateDescriptionHandler ─────────────────────────────────────────

    [Fact]
    public void UpdateDescription_ParsesAllFields()
    {
        var handler = new UpdateDescriptionHandler(_bus);
        var sourceLine = "LocalPlayer: ProcessUpdateDescription(12345, \"My Garden\", \"A lovely plot\", \"Watering\", 0, \"unused(Scale=0.5)\", 1)";
        var args = "(12345, \"My Garden\", \"A lovely plot\", \"Watering\", 0, \"unused(Scale=0.5)\", 1)";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        var frame = _bus.Published<UpdateDescriptionFrame>().Should().ContainSingle().Which;
        frame.PlotId.Should().Be(12345);
        frame.Title.ToString().Should().Be("My Garden");
        frame.Description.ToString().Should().Be("A lovely plot");
        frame.Action.ToString().Should().Be("Watering");
        frame.Scale.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void UpdateDescription_ZeroScale_WhenNoScaleToken()
    {
        var handler = new UpdateDescriptionHandler(_bus);
        var sourceLine = "LocalPlayer: ProcessUpdateDescription(99, \"T\", \"D\", \"A\", 0, \"noscalehere\", 1)";
        var args = "(99, \"T\", \"D\", \"A\", 0, \"noscalehere\", 1)";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        var frame = _bus.Published<UpdateDescriptionFrame>().Should().ContainSingle().Which;
        frame.PlotId.Should().Be(99);
        frame.Scale.Should().Be(0.0);
    }

    [Fact]
    public void UpdateDescription_EmptyStrings()
    {
        var handler = new UpdateDescriptionHandler(_bus);
        var sourceLine = "LocalPlayer: ProcessUpdateDescription(1, \"\", \"\", \"\", 0, \"unused(Scale=1.0)\", 0)";
        var args = "(1, \"\", \"\", \"\", 0, \"unused(Scale=1.0)\", 0)";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        var frame = _bus.Published<UpdateDescriptionFrame>().Should().ContainSingle().Which;
        frame.PlotId.Should().Be(1);
        frame.Title.Length.Should().Be(0);
        frame.Description.Length.Should().Be(0);
        frame.Action.Length.Should().Be(0);
        frame.Scale.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void UpdateDescription_MemorySlicesIntoSourceLog()
    {
        var handler = new UpdateDescriptionHandler(_bus);
        var sourceLine = "LocalPlayer: ProcessUpdateDescription(1, \"Herbs\", \"desc\", \"act\", 0, \"x(Scale=0.5)\", 1)";
        var args = sourceLine.AsSpan()["LocalPlayer: ProcessUpdateDescription".Length..];

        handler.Handle(args, default, sourceLine, Meta());

        var frame = _bus.Published<UpdateDescriptionFrame>().Should().ContainSingle().Which;
        frame.Title.ToString().Should().Be("Herbs");
    }

    // ── SetPetOwnerHandler ───────────────────────────────────────────────

    [Fact]
    public void SetPetOwner_ParsesEntityId()
    {
        var handler = new SetPetOwnerHandler(_bus);
        var args = "(25237464, 7, 0)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        var frame = _bus.Published<SetPetOwnerFrame>().Should().ContainSingle().Which;
        frame.EntityId.Should().Be(25237464);
    }

    [Fact]
    public void SetPetOwner_LargeEntityId()
    {
        var handler = new SetPetOwnerHandler(_bus);
        var args = "(9999999999, extra)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        _bus.Published<SetPetOwnerFrame>().Should().ContainSingle()
            .Which.EntityId.Should().Be(9999999999);
    }

    // ── ScreenTextHandler ────────────────────────────────────────────────

    [Fact]
    public void ScreenText_EmitsForErrorMessage()
    {
        var handler = new ScreenTextHandler(_bus);
        var args = "(ErrorMessage, \"Something went wrong\")";

        handler.Handle(args.AsSpan(), default, "", Meta());

        _bus.Published<ScreenTextErrorFrame>().Should().ContainSingle();
    }

    [Fact]
    public void ScreenText_IgnoresNonErrorMessage()
    {
        var handler = new ScreenTextHandler(_bus);
        var args = "(ImportantInfo, \"You discovered a point of interest!\")";

        handler.Handle(args.AsSpan(), default, "", Meta());

        _bus.Published<ScreenTextErrorFrame>().Should().BeEmpty();
    }

    [Fact]
    public void ScreenText_IgnoresEmptyArgs()
    {
        var handler = new ScreenTextHandler(_bus);

        handler.Handle(ReadOnlySpan<char>.Empty, default, "", Meta());

        _bus.Published<ScreenTextErrorFrame>().Should().BeEmpty();
    }

    // ── ErrorMessageHandler ──────────────────────────────────────────────

    [Fact]
    public void ErrorMessage_ExtractsSeedDisplayName()
    {
        var handler = new ErrorMessageHandler(_bus);
        var sourceLine = "LocalPlayer: ProcessErrorMessage(ItemUnusable, \"Onion Seeds can't be used: You already have the maximum of that type of plant growing\")";
        var args = "(ItemUnusable, \"Onion Seeds can't be used: You already have the maximum of that type of plant growing\")";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        var frame = _bus.Published<PlantingCapFrame>().Should().ContainSingle().Which;
        frame.SeedDisplayName.ToString().Should().Be("Onion Seeds");
    }

    [Fact]
    public void ErrorMessage_IgnoresNonPlantingCapErrors()
    {
        var handler = new ErrorMessageHandler(_bus);
        var args = "(ItemUnusable, \"Cannot use that item here\")";

        handler.Handle(args.AsSpan(), default, "", Meta());

        _bus.Published<PlantingCapFrame>().Should().BeEmpty();
    }

    [Fact]
    public void ErrorMessage_IgnoresEmptyArgs()
    {
        var handler = new ErrorMessageHandler(_bus);

        handler.Handle(ReadOnlySpan<char>.Empty, default, "", Meta());

        _bus.Published<PlantingCapFrame>().Should().BeEmpty();
    }

    [Fact]
    public void ErrorMessage_SingleWordSeedName()
    {
        var handler = new ErrorMessageHandler(_bus);
        var sourceLine = "LocalPlayer: ProcessErrorMessage(ItemUnusable, \"Broccoli can't be used: You already have the maximum of that type of plant growing\")";
        var args = "(ItemUnusable, \"Broccoli can't be used: You already have the maximum of that type of plant growing\")";

        handler.Handle(args.AsSpan(), default, sourceLine, Meta());

        _bus.Published<PlantingCapFrame>().Should().ContainSingle()
            .Which.SeedDisplayName.ToString().Should().Be("Broccoli");
    }

    // ── SpyEventBus ─────────────────────────────────────────────────────

    private sealed class SpyEventBus : IDomainEventBus
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
            => new NoopDisposable();

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_published.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _published[typeof(T)] = list;
            }
            list.Add(domainEvent);
        }

        public List<T> Published<T>() where T : struct
        {
            if (_published.TryGetValue(typeof(T), out var list))
                return list.Cast<T>().ToList();
            return [];
        }

        public void Clear() => _published.Clear();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
