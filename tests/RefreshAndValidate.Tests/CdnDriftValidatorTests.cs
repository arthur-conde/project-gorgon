using System.Text;
using FluentAssertions;
using Mithril.Reference;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.Tools.RefreshAndValidate.Tests;

[Trait("Category", "FileIO")]
public class CdnDriftValidatorTests
{
    private readonly ITestOutputHelper _output;

    public CdnDriftValidatorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Validator_succeeds_against_bundled_data()
    {
        var specs = ParserRegistry.Discover();
        var fetcher = new InMemoryFetcher(LoadBundledFiles(specs));
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var exit = await CdnDriftValidator.RunAsync(
            fetcher, "test-version", specs, new StringWriter(stdout), new StringWriter(stderr));

        _output.WriteLine(stdout.ToString());
        if (stderr.Length > 0) _output.WriteLine("--- STDERR ---\n" + stderr);

        exit.Should().Be(0, "bundled data is the canonical fixture and must always pass");
        stderr.ToString().Should().BeEmpty();
        stdout.ToString().Should().Contain($"All {specs.Count} files validated against CDN test-version.");
    }

    [Fact]
    public async Task Validator_fails_when_unknown_discriminator_injected()
    {
        var specs = ParserRegistry.Discover();
        var bundled = LoadBundledFiles(specs).ToDictionary(kv => kv.Key, kv => kv.Value);

        // Surgical edit: pick the first quest with at least one Requirement and
        // rewrite its [0].T to a value no QuestRequirement subclass discriminates.
        const string SyntheticT = "NotARealRequirementType";
        var quests = JObject.Parse(bundled["quests.json"]);
        var (questId, _) = MutateFirstQuestRequirement(quests, SyntheticT);
        bundled["quests.json"] = quests.ToString();

        var fetcher = new InMemoryFetcher(bundled);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var exit = await CdnDriftValidator.RunAsync(
            fetcher, "test-version", specs, new StringWriter(stdout), new StringWriter(stderr));

        _output.WriteLine(stdout.ToString());
        if (stderr.Length > 0) _output.WriteLine("--- STDERR ---\n" + stderr);

        exit.Should().Be(1, $"injected unknown T='{SyntheticT}' into quest {questId} must trip the gate");
        stdout.ToString().Should().Contain(SyntheticT);
        stderr.ToString().Should().Contain("quests.json");
    }

    private static IReadOnlyDictionary<string, string> LoadBundledFiles(IReadOnlyList<IParserSpec> specs)
    {
        var dict = new Dictionary<string, string>(specs.Count, StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BundledData", spec.FileName);
            File.Exists(path).Should().BeTrue(
                $"missing fixture {path}. Is the Content link in RefreshAndValidate.Tests.csproj correct?");
            dict[spec.FileName] = File.ReadAllText(path);
        }
        return dict;
    }

    private static (string QuestId, string PreviousT) MutateFirstQuestRequirement(JObject quests, string newT)
    {
        foreach (var prop in quests.Properties())
        {
            if (prop.Value is not JObject quest) continue;
            if (quest["Requirements"] is not JArray reqs || reqs.Count == 0) continue;
            if (reqs[0] is not JObject req || req["T"] is not JValue t) continue;

            var prev = t.Value<string>() ?? "";
            req["T"] = newT;
            return (prop.Name, prev);
        }
        throw new InvalidOperationException("no quest with a Requirements[0].T value found in bundled quests.json");
    }
}
