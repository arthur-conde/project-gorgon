using FluentAssertions;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

/// <summary>
/// #248 Option A — the colour-strip helper. The PlayerTitle <c>Title</c> field
/// wraps ~every label in a <c>&lt;color&gt;</c> span the shared
/// <c>FormattedText</c> renderer does not parse; this helper removes the cosmetic
/// markup in the row / detail projection. Drift-safe: malformed / unbalanced
/// forms pass through rather than throw (same forgiving contract as the renderer).
/// </summary>
public sealed class TitleColorMarkupTests
{
    [Theory]
    [InlineData("<color=cyan>Game Admin</color>", "Game Admin")]
    [InlineData("<color=white>Content Creator</color>", "Content Creator")]
    [InlineData("<color=#00cc00>Warsmith</color>", "Warsmith")]
    [InlineData("<color=yellow>Insane</color>", "Insane")]
    public void Strip_RemovesWellFormedColorSpan(string raw, string expected)
    {
        TitleColorMarkup.Strip(raw).Should().Be(expected);
    }

    [Fact]
    public void Strip_PreservesNestedInnerMarkup()
    {
        // Inner <b> survives so the body still flows through the shared renderer.
        TitleColorMarkup.Strip("<color=red>The <b>Bold</b> One</color>")
            .Should().Be("The <b>Bold</b> One");
    }

    [Fact]
    public void Strip_NoColorTag_ReturnsInputUnchanged()
    {
        const string plain = "Just A Title";
        TitleColorMarkup.Strip(plain).Should().BeSameAs(plain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Strip_NullOrEmpty_ReturnsAsIs(string? raw)
    {
        TitleColorMarkup.Strip(raw).Should().Be(raw);
    }

    [Fact]
    public void Strip_UnclosedOpenTag_StillRemovesOpenTag()
    {
        // A lone <color=...> with no </color> — the label is cleaner without the
        // dangling open tag; the surrounding renderer tolerates unbalanced markup.
        TitleColorMarkup.Strip("<color=red>Dangling").Should().Be("Dangling");
    }

    [Fact]
    public void Strip_LoneCloseTag_IsRemoved()
    {
        TitleColorMarkup.Strip("Orphan</color>").Should().Be("Orphan");
    }

    [Fact]
    public void Strip_MalformedOpenTag_NeverClosed_PassesThroughLiterally()
    {
        // "<color" with no closing '>' anywhere — don't swallow the tail; render
        // it verbatim (defensive, no throw).
        TitleColorMarkup.Strip("<colorbroken text").Should().Be("<colorbroken text");
    }

    [Fact]
    public void Strip_MultipleSpans_AllRemoved()
    {
        TitleColorMarkup.Strip("<color=a>One</color> and <color=b>Two</color>")
            .Should().Be("One and Two");
    }
}
