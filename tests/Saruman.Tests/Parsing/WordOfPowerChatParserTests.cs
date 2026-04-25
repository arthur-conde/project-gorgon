using System.IO;
using FluentAssertions;
using Mithril.Shared.Character;
using Saruman.Domain;
using Saruman.Parsing;
using Saruman.Services;
using Saruman.Settings;
using Xunit;

namespace Saruman.Tests.Parsing;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class WordOfPowerChatParserTests
{
    private static WordOfPowerChatParser NewParser(params string[] trackedCodes)
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-chat");
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var store = new PerCharacterStore<SarumanState>(dir, "saruman.json", SarumanJsonContext.Default.SarumanState);
        var view = new PerCharacterView<SarumanState>(active, store);
        var svc = new SarumanCodebookService(view);
        foreach (var c in trackedCodes)
            svc.RecordDiscovery(new WordOfPowerDiscovered(DateTime.UtcNow, c, $"Effect for {c}", "desc"));
        return new WordOfPowerChatParser(svc);
    }

    [Fact]
    public void Matches_tracked_code_in_Nearby_channel()
    {
        var p = NewParser("PRYSGWIMLIK");
        var evt = p.TryParse("26-04-22 02:39:57\t[Nearby] Hikaratu: PRYSGWIMLIK", DateTime.UtcNow);

        evt.Should().BeOfType<WordOfPowerSpoken>();
        var s = (WordOfPowerSpoken)evt!;
        s.Code.Should().Be("PRYSGWIMLIK");
        s.Speaker.Should().Be("Hikaratu");
    }

    [Fact]
    public void Matches_tracked_code_on_any_channel()
    {
        var p = NewParser("FEAVEG");
        var evt = p.TryParse("26-04-22 00:02:54\t[Guild] KiraAzure: hey I just learned FEAVEG", DateTime.UtcNow);

        evt.Should().BeOfType<WordOfPowerSpoken>();
        ((WordOfPowerSpoken)evt!).Code.Should().Be("FEAVEG");
    }

    [Fact]
    public void Rejects_uppercase_that_is_not_in_codebook()
    {
        var p = NewParser("FEAVEG");
        p.TryParse("26-04-22 02:42:50\t[Nearby] Hellpuppy: MUAHAHAH", DateTime.UtcNow).Should().BeNull();
        p.TryParse("26-04-22 00:04:54\t[Guild] Laky: HOOOWL", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Rejects_line_with_no_uppercase_token()
    {
        var p = NewParser("FEAVEG");
        p.TryParse("26-04-22 00:01:09\t[Help] jmhbnz: Is it safe?", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Rejects_empty_line()
    {
        var p = NewParser("FEAVEG");
        p.TryParse("", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Handles_missing_speaker_format_gracefully()
    {
        var p = NewParser("FEAVEG");
        // Line without the standard timestamp/channel prefix: still matches tracked code.
        var evt = p.TryParse("FEAVEG was spoken", DateTime.UtcNow);
        evt.Should().BeOfType<WordOfPowerSpoken>();
    }
}
