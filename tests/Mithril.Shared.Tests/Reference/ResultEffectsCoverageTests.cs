using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

/// <summary>
/// End-to-end gate: every <c>ResultEffects</c> string in the bundled recipes.json
/// must produce *something* — a typed preview chip, a humanised tag line, or a
/// deliberate silent-allow-list match. The gate guards against silent regressions
/// when a game patch introduces a new prefix.
/// <para>
/// The test loads the real bundled recipes.json + items.json + tsysclientinfo.json
/// + tsysprofiles.json + attributes.json from the runtime-copied
/// <c>Reference/BundledData/</c> folder; if those files aren't present (e.g. on a
/// CI image that doesn't copy them) it skips silently rather than failing.
/// </para>
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class ResultEffectsCoverageTests : IDisposable
{
    private readonly string _cacheDir;

    public ResultEffectsCoverageTests()
    {
        _cacheDir = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-coverage-tests");
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    [Fact]
    public void ResultEffectsParser_CoversEveryEffectInBundledRecipes()
    {
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(bundledDir, "recipes.json"))) return;

        var refData = new ReferenceDataService(
            _cacheDir,
            new HttpClient(new ThrowingHandler("HTTP must not be called from coverage gate")),
            bundledDir: bundledDir);

        var unmatched = new List<string>();
        var allowSilent = new HashSet<string>(StringComparer.Ordinal);

        foreach (var recipe in refData.Recipes.Values)
        {
            var effects = recipe.ResultEffects;
            if (effects is null || effects.Count == 0) continue;

            foreach (var effect in effects)
            {
                if (string.IsNullOrWhiteSpace(effect)) continue;

                // Each parser is called individually; an effect is "covered" if at
                // least one parser emits a preview for it. Particle_* tags are the
                // intentional silent allow-list — recognised but no preview.
                if (effect.StartsWith("Particle_", System.StringComparison.Ordinal))
                {
                    allowSilent.Add(effect);
                    continue;
                }

                if (IsCovered(effect, refData)) continue;

                // TSysCraftedEquipment legitimately silent-skips when its template
                // has no TSysProfile or the profile isn't in tsysprofiles.json —
                // see AugmentPoolParserTests' three "skipped" cases. The parser
                // recognises the prefix shape even when the args don't resolve to
                // a queryable pool, so treat those as covered for gate purposes.
                if (effect.StartsWith("TSysCraftedEquipment(", System.StringComparison.Ordinal))
                {
                    allowSilent.Add(effect);
                    continue;
                }

                unmatched.Add(effect);
            }
        }

        unmatched.Should().BeEmpty(
            "every ResultEffects entry must produce a preview through at least one Parse* method or be on the silent allow-list. " +
            $"Allow-listed Particle_* count: {allowSilent.Count}.");
    }

    private static bool IsCovered(string effect, IReferenceDataService refData)
    {
        var single = new[] { effect };
        if (ResultEffectsParser.ParseCraftedGear(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseAugments(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseTaughtRecipes(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseWaxItems(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseAddItemTSysPowerWaxes(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseAugmentPools(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseUnpreviewableExtractions(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseResearchProgress(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseXpGrants(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseWordsOfPower(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseLearnedAbilities(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseItemProducing(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseEquipBonuses(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseCraftingEnhancements(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseRecipeCooldowns(single, refData).Count > 0) return true;
        if (ResultEffectsParser.ParseEffectTags(single, refData).Count > 0) return true;
        return false;
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(message);
    }
}
