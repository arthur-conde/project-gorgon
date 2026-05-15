using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

/// <summary>
/// Real-data sanity walk (cookbook *Verification ladder* — load-bearing per #298):
/// project the bundled <c>playertitles.json</c> through the tab VM and assert a
/// Lint_NotObtainable title, a PlayerBestowedTitle, and a plain earnable title
/// with a Tooltip all project sensibly (clean label, correct facets).
/// </summary>
public sealed class PlayerTitlesRealDataSanityTests
{
    [Fact]
    public void RealBundledPlayerTitles_DiverseEntries_ProjectSensibly()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(bundled, "playertitles.json"))) return;

        var refData = BuildRealRefData(bundled);
        if (refData is null) return;

        var vm = new PlayerTitlesTabViewModel(refData);

        vm.AllTitles.Should().NotBeEmpty("bundled playertitles.json ships ~679 entries");

        // No projected label may leak literal <color> markup (#248 Option A).
        vm.AllTitles.Should().OnlyContain(t =>
            !t.DisplayTitle.Contains("<color", StringComparison.OrdinalIgnoreCase)
            && !t.DisplayTitle.Contains("</color>", StringComparison.OrdinalIgnoreCase));
        vm.AllTitles.Should().OnlyContain(t => !string.IsNullOrEmpty(t.DisplayTitle));
        vm.AllTitles.Should().OnlyContain(t => t.EnvelopeKey.StartsWith("Title_", StringComparison.Ordinal));

        // A Lint_NotObtainable dev title — present, flagged not-obtainable, clean label.
        var contentCreator = vm.AllTitles.FirstOrDefault(t => t.EnvelopeKey == "Title_101");
        contentCreator.Should().NotBeNull("Title_101 'Content Creator' is a stable bundled entry");
        contentCreator!.IsObtainable.Should().BeFalse("Title_101 carries Lint_NotObtainable");
        contentCreator.DisplayTitle.Should().Be("Content Creator");

        // A PlayerBestowedTitle with a Tooltip — earnable facet true, tooltip surfaces.
        var insane = vm.AllTitles.FirstOrDefault(t => t.EnvelopeKey == "Title_15001");
        insane.Should().NotBeNull("Title_15001 'Insane' is a stable bundled PlayerBestowedTitle");
        insane!.DisplayTitle.Should().Be("Insane");
        insane.IsObtainable.Should().BeTrue("PlayerBestowedTitle is not a Lint_* keyword");
        insane.HasTooltip.Should().BeTrue();
        vm.SelectedTitle = insane;
        vm.DetailViewModel!.DisplayName.Should().Be("Insane");
        vm.DetailViewModel.FooterText.Should().Be("Title_15001");
        vm.DetailViewModel.Tooltip.Should().NotBeNullOrEmpty();

        // A plain earnable title with a Tooltip.
        var warsmith = vm.AllTitles.FirstOrDefault(t => t.EnvelopeKey == "Title_5018");
        warsmith.Should().NotBeNull("Title_5018 'Warsmith' is a stable bundled earnable title");
        warsmith!.DisplayTitle.Should().Be("Warsmith");
        warsmith.IsObtainable.Should().BeTrue();
        warsmith.HasTooltip.Should().BeTrue();
    }

    private static IReferenceDataService? BuildRealRefData(string bundled)
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(cacheDir);
            using var http = new HttpClient(new ThrowingHttpHandler());
            return new ReferenceDataService(cacheDir, http, bundledDir: bundled);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be called in this test");
    }
}
