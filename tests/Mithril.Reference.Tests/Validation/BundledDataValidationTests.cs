using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mithril.Reference;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.Reference.Tests.Validation;

/// <summary>
/// Convention-driven validation harness. Reflects every <see cref="IParserSpec"/>
/// shipped in <c>Mithril.Reference</c> and runs three gates against each:
/// (1) parses without throwing, (2) entry count meets the spec's floor, and
/// (3) the parsed graph contains zero <c>IUnknownDiscriminator</c> sentinels.
/// Adding a new BundledData file is just dropping a new <c>IParserSpec</c>
/// implementation — this test picks it up at next run.
/// </summary>
public class BundledDataValidationTests
{
    private readonly ITestOutputHelper _output;

    public BundledDataValidationTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> ParserSpecFileNames
        => ParserRegistry.Discover()
            .Select(s => new object[] { s.FileName })
            .ToArray();

    [Theory]
    [MemberData(nameof(ParserSpecFileNames))]
    public void Bundled_File_Parses_With_Zero_Unknowns(string fileName)
    {
        var spec = ParserRegistry.Discover()
            .Single(s => string.Equals(s.FileName, fileName, StringComparison.Ordinal));

        var path = Path.Combine(AppContext.BaseDirectory, "BundledData", spec.FileName);
        File.Exists(path).Should().BeTrue(
            $"Expected bundled fixture at {path}. Is the Content link in the test csproj correct?");

        var json = File.ReadAllText(path);

        var parsed = spec.Parse(json);

        var count = spec.CountEntries(parsed);
        count.Should().BeGreaterThanOrEqualTo(
            spec.MinimumEntryCount,
            $"{spec.FileName} should contain at least {spec.MinimumEntryCount} entries");

        // Take up to 20 unknowns so the failing message is bounded but rich;
        // a flood of unknowns would otherwise drown the test output.
        var unknowns = spec.EnumerateUnknowns(parsed).Take(20).ToList();

        if (unknowns.Count > 0)
        {
            _output.WriteLine($"--- Unknowns found in {spec.FileName} ({unknowns.Count} reported, possibly more) ---");
            foreach (var u in unknowns)
                _output.WriteLine($"  {u}");
        }

        unknowns.Should().BeEmpty(
            $"{spec.FileName} should have full discriminator coverage. " +
            $"First {unknowns.Count} unknown(s): {string.Join("; ", unknowns)}");
    }
}
