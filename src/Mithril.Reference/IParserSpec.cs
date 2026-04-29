using System.Collections.Generic;

namespace Mithril.Reference;

/// <summary>
/// Convention-discovered parser specification for one BundledData file. The
/// validation harness (<c>BundledDataValidationTests</c>) reflects over the
/// <c>Mithril.Reference</c> assembly to find every <see cref="IParserSpec"/>
/// implementation and runs the standard suite of gates (parse without
/// throwing, expected entry count, no <see cref="Models.IUnknownDiscriminator"/>
/// sentinels) against each.
/// </summary>
/// <remarks>
/// To add a new BundledData source: implement this interface in a class with
/// a parameterless constructor in the <c>Mithril.Reference</c> assembly. The
/// theory test picks it up automatically at next test run; no per-file test
/// boilerplate.
/// </remarks>
public interface IParserSpec
{
    /// <summary>Bundled file name (e.g. <c>"quests.json"</c>).</summary>
    string FileName { get; }

    /// <summary>
    /// Lower bound on the number of top-level entries expected. Used as a
    /// sanity gate to catch silent truncation by a faulty converter. Use
    /// <c>0</c> if you don't want the count gate.
    /// </summary>
    int MinimumEntryCount { get; }

    /// <summary>
    /// Parses the supplied JSON content and returns the parsed object graph
    /// (typically <c>IReadOnlyDictionary&lt;string, TEntry&gt;</c>).
    /// </summary>
    object Parse(string json);

    /// <summary>
    /// Returns the number of top-level entries in <paramref name="parsed"/>
    /// for the count-gate assertion.
    /// </summary>
    int CountEntries(object parsed);

    /// <summary>
    /// Walks the parsed graph and yields every <see cref="Models.IUnknownDiscriminator"/>
    /// instance encountered, paired with a path string for diagnostics
    /// (e.g. <c>"quest_172/Requirements[0]"</c>). Empty enumeration means
    /// full discriminator coverage.
    /// </summary>
    IEnumerable<UnknownReport> EnumerateUnknowns(object parsed);
}

/// <summary>
/// Diagnostic record yielded by <see cref="IParserSpec.EnumerateUnknowns"/>
/// when an unrecognised discriminator value is encountered. <see cref="Path"/>
/// is a human-readable locator like <c>"quest_172/Requirements[0]"</c> so the
/// failing test message points directly at the JSON entry that needs a new
/// subclass.
/// </summary>
public readonly struct UnknownReport
{
    public UnknownReport(string path, string discriminatorValue, string baseTypeName)
    {
        Path = path;
        DiscriminatorValue = discriminatorValue;
        BaseTypeName = baseTypeName;
    }

    public string Path { get; }
    public string DiscriminatorValue { get; }
    public string BaseTypeName { get; }

    public override string ToString()
        => $"{BaseTypeName} unknown discriminator '{DiscriminatorValue}' at {Path}";
}
